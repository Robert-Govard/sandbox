namespace Editor;

/// <summary>
/// Utility for saving a downloaded map and all its dependencies
/// into the project's Assets/Maps directory so the map can be opened
/// and edited in Hammer.
///
/// Strategy:
/// 1. Snapshot diff — take a snapshot of FileSystem.Mounted before and after
///    mounting the package. The diff is exactly the new files. Works for
///    newly downloaded maps.
/// 2. Direct mounted FS lookup — for already-mounted maps (built-in or cached),
///    look for maps/{packagename}.vpk and deps in maps/{packagename}/.
///    The VPK filename in the virtual FS matches the package name part of the ident.
/// 3. Package download cache dir on disk — {sbox_root}/download/assets/{ident_dir}/
/// 4. AssetSystem.GetPackageFiles / FindByPath — for non-map asset packages
/// </summary>
public static class MapProjectSaver
{
	/// <summary>
	/// Save a map package with all its files to the project's Assets/Maps directory.
	/// Returns the number of files copied.
	/// </summary>
	public static async System.Threading.Tasks.Task<int> SaveWithDependencies( string mapIdent )
	{
		if ( string.IsNullOrWhiteSpace( mapIdent ) ) return 0;

		try
		{
			var targetRoot = GetMapsDirectory();

			// Extract the package name part from the ident
			// e.g. "facepunch.flatgrass" -> "flatgrass"
			var packageName = GetPackageName( mapIdent );

			// Step 1: Snapshot all files visible in the mounted filesystem BEFORE mounting
			var beforeFiles = SnapshotMountedFiles();
			Log.Info( $"MapProjectSaver: Snapshot before mount: {beforeFiles.Count} file(s)" );

			// Step 2: Fetch and mount the package (downloads to cache if needed)
			var package = await Package.FetchAsync( mapIdent, false );
			if ( package == null || package.Revision == null )
			{
				Log.Warning( $"MapProjectSaver: Package not found: '{mapIdent}'" );
				return 0;
			}

			await package.MountAsync();

			// Step 3: Try AssetSystem install for non-map packages
			if ( AssetSystem.CanCloudInstall( package ) && !AssetSystem.IsCloudInstalled( package ) )
			{
				await AssetSystem.InstallAsync( package.FullIdent );
			}

			// Step 4: Snapshot AFTER mounting and diff
			var afterFiles = SnapshotMountedFiles();
			var newFiles = new HashSet<string>( afterFiles );
			newFiles.ExceptWith( beforeFiles );

			Log.Info( $"MapProjectSaver: Snapshot after mount: {afterFiles.Count} file(s), new: {newFiles.Count} file(s)" );

			var copiedCount = 0;

			// Approach 1: Copy files discovered by the before/after diff
			if ( newFiles.Count > 0 )
			{
				copiedCount = CopyNewMountedFiles( newFiles, targetRoot );
				if ( copiedCount > 0 )
				{
					Log.Info( $"MapProjectSaver: Saved '{mapIdent}' via mount diff ({copiedCount} file(s))" );
					return copiedCount;
				}
			}

			// Approach 2: Direct lookup in FileSystem.Mounted for already-mounted maps
			// After mounting, the map VPK is accessible as maps/{packagename}.vpk
			// and dependencies may be in maps/{packagename}/
			copiedCount = CopyViaDirectLookup( packageName, targetRoot );
			if ( copiedCount > 0 )
			{
				Log.Info( $"MapProjectSaver: Saved '{mapIdent}' via direct lookup ({copiedCount} file(s))" );
				return copiedCount;
			}

			// Approach 3: Copy from the package-specific download cache directory on disk
			copiedCount = CopyViaPackageDir( mapIdent, targetRoot );
			if ( copiedCount > 0 )
			{
				Log.Info( $"MapProjectSaver: Saved '{mapIdent}' via package dir ({copiedCount} file(s))" );
				return copiedCount;
			}

			// Approach 4: AssetSystem.GetPackageFiles (works for non-map asset packages)
			copiedCount = CopyViaPackageFiles( package, targetRoot );
			if ( copiedCount > 0 )
			{
				Log.Info( $"MapProjectSaver: Saved '{mapIdent}' via PackageFiles ({copiedCount} file(s))" );
				return copiedCount;
			}

			// Approach 5: AssetSystem.FindByPath + GetReferences
			copiedCount = CopyViaAssetSystem( mapIdent, targetRoot );
			if ( copiedCount > 0 )
			{
				Log.Info( $"MapProjectSaver: Saved '{mapIdent}' via AssetSystem ({copiedCount} file(s))" );
				return copiedCount;
			}

			Log.Warning( $"MapProjectSaver: Could not find any files for '{mapIdent}'" );
			return 0;
		}
		catch ( System.Exception ex )
		{
			Log.Error( $"MapProjectSaver: Failed to save map '{mapIdent}': {ex.Message}" );
			return 0;
		}
	}

	/// <summary>
	/// Get the target directory for maps: project Assets/Maps/
	/// </summary>
	public static string GetMapsDirectory()
	{
		var assetsPath = Project.Current.GetAssetsPath();
		var mapsDir = System.IO.Path.Combine( assetsPath, "Maps" );
		System.IO.Directory.CreateDirectory( mapsDir );
		return mapsDir;
	}

	/// <summary>
	/// Extract the package name part from a map ident.
	/// e.g. "facepunch.flatgrass" -> "flatgrass"
	/// e.g. "softsplit.gm_bigcity" -> "gm_bigcity"
	/// </summary>
	private static string GetPackageName( string mapIdent )
	{
		var name = mapIdent;
		if ( name.Contains( '.' ) )
			name = name[( name.IndexOf( '.' ) + 1 )..];
		return name;
	}

	/// <summary>
	/// Take a snapshot of all files visible in FileSystem.Mounted under common
	/// resource directories. We only scan directories where map assets live
	/// (maps/, materials/, models/, textures/, sounds/, particles/, etc.)
	/// to keep the snapshot fast and relevant.
	/// </summary>
	private static HashSet<string> SnapshotMountedFiles()
	{
		var files = new HashSet<string>( System.StringComparer.OrdinalIgnoreCase );

		var scanDirs = new[] { "maps", "materials", "models", "textures", "sounds", "particles", "shaders", "ui" };

		foreach ( var dir in scanDirs )
		{
			if ( !FileSystem.Mounted.DirectoryExists( dir ) ) continue;

			foreach ( var file in FileSystem.Mounted.FindFile( dir, "*", true ) )
			{
				files.Add( $"{dir}/{file}" );
			}
		}

		return files;
	}

	/// <summary>
	/// Copy files discovered by the before/after mount diff from the mounted
	/// filesystem to the project's Assets/Maps directory.
	/// Map VPK files go to the root of Maps/. Dependency files (materials, models,
	/// etc.) preserve their directory structure under Maps/.
	/// </summary>
	private static int CopyNewMountedFiles( HashSet<string> newFiles, string targetRoot )
	{
		var copiedCount = 0;

		foreach ( var mountedPath in newFiles )
		{
			if ( !FileSystem.Mounted.FileExists( mountedPath ) ) continue;

			var data = FileSystem.Mounted.ReadAllBytes( mountedPath );
			if ( data.Length == 0 ) continue;

			// Build the target relative path:
			// - maps/xxx.vpk -> xxx.vpk  (map VPKs go to Maps/ root)
			// - materials/... -> materials/...  (deps keep their structure under Maps/)
			string targetRelative;
			if ( mountedPath.StartsWith( "maps/", System.StringComparison.OrdinalIgnoreCase ) )
				targetRelative = mountedPath[5..];
			else
				targetRelative = mountedPath;

			var targetPath = System.IO.Path.Combine( targetRoot, targetRelative );
			var targetDir = System.IO.Path.GetDirectoryName( targetPath );

			if ( !string.IsNullOrEmpty( targetDir ) )
				System.IO.Directory.CreateDirectory( targetDir );

			if ( !System.IO.File.Exists( targetPath ) )
			{
				System.IO.File.WriteAllBytes( targetPath, data.ToArray() );
				copiedCount++;
				Log.Info( $"MapProjectSaver: Extracted '{mountedPath}' from mounted FS ({data.Length} bytes)" );
			}
		}

		return copiedCount;
	}

	/// <summary>
	/// Direct lookup in FileSystem.Mounted for already-mounted maps.
	/// After mounting, the map VPK is accessible as maps/{packagename}.vpk
	/// and dependencies may be in maps/{packagename}/ subdirectory.
	///
	/// We also scan for companion files like {packagename}_baked.vpk,
	/// {packagename}_bakeresourcecache.vpk, etc.
	/// </summary>
	private static int CopyViaDirectLookup( string packageName, string targetRoot )
	{
		var copiedCount = 0;
		var packageNameLower = packageName.ToLowerInvariant();

		// Find VPK files in maps/ that match the package name
		foreach ( var file in FileSystem.Mounted.FindFile( "maps/", "*.vpk", true ) )
		{
			var fileLower = file.ToLowerInvariant();

			// Match: exact name or name starting with packagename_ (e.g. flatgrass_baked.vpk)
			var baseName = fileLower;
			if ( baseName.EndsWith( ".vpk" ) )
				baseName = baseName[..^4];

			// Check if this VPK belongs to our map
			// e.g. packageName="flatgrass" matches "flatgrass.vpk", "flatgrass_baked.vpk"
			// but NOT "other_flatgrass.vpk"
			if ( baseName != packageNameLower && !baseName.StartsWith( packageNameLower + "_" ) )
				continue;

			var mountedPath = $"maps/{file}";
			if ( !FileSystem.Mounted.FileExists( mountedPath ) ) continue;

			var data = FileSystem.Mounted.ReadAllBytes( mountedPath );
			if ( data.Length == 0 ) continue;

			// Strip "maps/" prefix for target path
			var targetRelative = file;
			if ( targetRelative.StartsWith( "maps/", System.StringComparison.OrdinalIgnoreCase ) )
				targetRelative = targetRelative[5..];

			var targetPath = System.IO.Path.Combine( targetRoot, targetRelative );
			var targetDir = System.IO.Path.GetDirectoryName( targetPath );

			if ( !string.IsNullOrEmpty( targetDir ) )
				System.IO.Directory.CreateDirectory( targetDir );

			if ( !System.IO.File.Exists( targetPath ) )
			{
				System.IO.File.WriteAllBytes( targetPath, data.ToArray() );
				copiedCount++;
				Log.Info( $"MapProjectSaver: Extracted '{mountedPath}' via direct lookup ({data.Length} bytes)" );
			}
		}

		// Also check for dependency directories: maps/{packagename}/
		var depDir = $"maps/{packageName}";
		if ( FileSystem.Mounted.DirectoryExists( depDir ) )
		{
			copiedCount += CopyMountedDirectoryRecursive( depDir, targetRoot );
		}

		return copiedCount;
	}

	/// <summary>
	/// Recursively copy all files from a mounted filesystem directory to the target.
	/// Strips the "maps/" prefix from the relative path since targetRoot is already Assets/Maps/.
	/// </summary>
	private static int CopyMountedDirectoryRecursive( string mountedDir, string targetRoot )
	{
		var copiedCount = 0;

		foreach ( var file in FileSystem.Mounted.FindFile( mountedDir, "*", true ) )
		{
			var mountedPath = $"{mountedDir}/{file}";
			if ( !FileSystem.Mounted.FileExists( mountedPath ) ) continue;

			var data = FileSystem.Mounted.ReadAllBytes( mountedPath );
			if ( data.Length == 0 ) continue;

			// Build relative path: "maps/flatgrass/materials/foo.vmat" -> "flatgrass/materials/foo.vmat"
			var targetRelative = mountedPath;
			if ( targetRelative.StartsWith( "maps/", System.StringComparison.OrdinalIgnoreCase ) )
				targetRelative = targetRelative[5..];

			var targetPath = System.IO.Path.Combine( targetRoot, targetRelative );
			var targetDir = System.IO.Path.GetDirectoryName( targetPath );

			if ( !string.IsNullOrEmpty( targetDir ) )
				System.IO.Directory.CreateDirectory( targetDir );

			if ( !System.IO.File.Exists( targetPath ) )
			{
				System.IO.File.WriteAllBytes( targetPath, data.ToArray() );
				copiedCount++;
			}
		}

		if ( copiedCount > 0 )
			Log.Info( $"MapProjectSaver: Extracted {copiedCount} dependency file(s) from '{mountedDir}/'" );

		return copiedCount;
	}

	/// <summary>
	/// Copy files from the package-specific download cache directory on disk.
	/// After MountAsync(), packages are downloaded to:
	///   {sbox_root}/download/assets/{org}_{package}/
	/// The directory name is the full ident with dots replaced by underscores.
	/// </summary>
	private static int CopyViaPackageDir( string mapIdent, string targetRoot )
	{
		var sboxRoot = GetSboxRoot();
		if ( string.IsNullOrEmpty( sboxRoot ) || !System.IO.Directory.Exists( sboxRoot ) )
			return 0;

		var downloadAssets = System.IO.Path.Combine( sboxRoot, "download", "assets" );
		if ( !System.IO.Directory.Exists( downloadAssets ) )
			return 0;

		// The download directory uses the full ident with dots -> underscores
		// e.g. "softsplit.gm_bigcity" -> "softsplit_gm_bigcity"
		var identDirName = mapIdent.Replace( '.', '_' );

		var packageDir = System.IO.Path.Combine( downloadAssets, identDirName );
		Log.Info( $"MapProjectSaver: Checking package dir '{packageDir}' exists={System.IO.Directory.Exists( packageDir )}" );

		if ( !System.IO.Directory.Exists( packageDir ) )
			return 0;

		var copiedCount = 0;

		foreach ( var file in System.IO.Directory.GetFiles( packageDir, "*", System.IO.SearchOption.AllDirectories ) )
		{
			var relPath = file[( packageDir.Length + 1 )..];
			// Skip thumbnails — they're not needed for the map to work
			if ( relPath.StartsWith( "thumb", System.StringComparison.OrdinalIgnoreCase ) ) continue;

			if ( TryCopyFile( file, relPath, targetRoot ) )
				copiedCount++;
		}

		return copiedCount;
	}

	/// <summary>
	/// Get the sbox install root directory by using FileSystem.Root.
	/// </summary>
	private static string GetSboxRoot()
	{
		return FileSystem.Root.GetFullPath( "/" ) ?? "";
	}

	/// <summary>
	/// Use AssetSystem.GetPackageFiles() + FileSystem.Cloud
	/// Works for regular asset packages (models, materials, etc.) but NOT for maps.
	/// </summary>
	private static int CopyViaPackageFiles( Package package, string targetRoot )
	{
		var files = AssetSystem.GetPackageFiles( package );
		if ( files == null || !files.Any() ) return 0;

		var copiedCount = 0;

		foreach ( var relativePath in files )
		{
			var sourcePath = FileSystem.Cloud.GetFullPath( relativePath );
			if ( string.IsNullOrEmpty( sourcePath ) || !System.IO.File.Exists( sourcePath ) )
			{
				sourcePath = FileSystem.Mounted.GetFullPath( relativePath );
			}

			if ( string.IsNullOrEmpty( sourcePath ) || !System.IO.File.Exists( sourcePath ) )
				continue;

			if ( TryCopyFile( sourcePath, relativePath, targetRoot ) )
				copiedCount++;
		}

		return copiedCount;
	}

	/// <summary>
	/// Try to find the asset via AssetSystem.FindByPath
	/// and copy it with references (textures, materials, etc.)
	/// </summary>
	private static int CopyViaAssetSystem( string mapIdent, string targetRoot )
	{
		var packageName = GetPackageName( mapIdent );

		var candidates = new List<string>();

		if ( !packageName.StartsWith( "maps/" ) )
			candidates.Add( $"maps/{packageName}.vpk" );

		if ( mapIdent.EndsWith( ".vpk" ) )
			candidates.Add( mapIdent );
		else
			candidates.Add( $"{mapIdent}.vpk" );

		foreach ( var candidate in candidates )
		{
			var asset = AssetSystem.FindByPath( candidate );
			if ( asset == null ) continue;

			var copiedFiles = new HashSet<string>();
			var copiedCount = 0;
			CopyAssetRecursive( asset, targetRoot, copiedFiles, ref copiedCount );
			return copiedCount;
		}

		return 0;
	}

	/// <summary>
	/// Copy a single file from source to targetRoot, preserving directory structure.
	/// Strips "maps/" prefix from relativePath since targetRoot is already Assets/Maps/.
	/// Returns true if the file was copied, false if it already existed or failed.
	/// </summary>
	private static bool TryCopyFile( string sourcePath, string relativePath, string targetRoot )
	{
		var targetRelative = relativePath;

		// Strip "maps/" prefix - targetRoot already is Assets/Maps/
		if ( targetRelative.StartsWith( "maps/", System.StringComparison.OrdinalIgnoreCase ) )
		{
			targetRelative = targetRelative[5..];
		}

		var targetPath = System.IO.Path.Combine( targetRoot, targetRelative );
		var targetDir = System.IO.Path.GetDirectoryName( targetPath );

		if ( !string.IsNullOrEmpty( targetDir ) )
		{
			System.IO.Directory.CreateDirectory( targetDir );
		}

		if ( System.IO.File.Exists( targetPath ) )
			return false;

		System.IO.File.Copy( sourcePath, targetPath, overwrite: false );
		return true;
	}

	/// <summary>
	/// Recursively copy an asset and all its references to the target directory.
	/// Used when AssetSystem can find the asset (non-map packages).
	/// </summary>
	private static void CopyAssetRecursive( Asset asset, string targetRoot, HashSet<string> copiedFiles, ref int copiedCount )
	{
		if ( asset == null ) return;

		var assetPath = asset.Path ?? asset.AbsolutePath ?? "";
		if ( !string.IsNullOrEmpty( assetPath ) && copiedFiles.Contains( assetPath ) ) return;
		if ( !string.IsNullOrEmpty( assetPath ) ) copiedFiles.Add( assetPath );

		if ( asset.IsCloud || asset.IsProcedural || asset.IsTransient ) return;

		var compiledPath = asset.GetCompiledFile( true );
		if ( string.IsNullOrEmpty( compiledPath ) || !System.IO.File.Exists( compiledPath ) )
			compiledPath = asset.AbsolutePath;

		if ( string.IsNullOrEmpty( compiledPath ) || !System.IO.File.Exists( compiledPath ) )
			return;

		var relativePath = asset.RelativePath ?? asset.Path ?? System.IO.Path.GetFileName( compiledPath );

		if ( relativePath.StartsWith( "maps/", System.StringComparison.OrdinalIgnoreCase ) )
			relativePath = relativePath[5..];

		var targetPath = System.IO.Path.Combine( targetRoot, relativePath );
		var targetDir = System.IO.Path.GetDirectoryName( targetPath );

		if ( !string.IsNullOrEmpty( targetDir ) )
			System.IO.Directory.CreateDirectory( targetDir );

		if ( !System.IO.File.Exists( targetPath ) )
		{
			System.IO.File.Copy( compiledPath, targetPath, overwrite: false );
			copiedCount++;
		}

		foreach ( var reference in asset.GetReferences( false ) )
		{
			CopyAssetRecursive( reference, targetRoot, copiedFiles, ref copiedCount );
		}
	}
}
