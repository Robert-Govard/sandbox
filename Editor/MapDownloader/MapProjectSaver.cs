namespace Editor;

/// <summary>
/// Utility for saving a downloaded map and all its dependencies
/// into the project's Assets/Maps directory so the map can be opened
/// and edited in Hammer.
///
/// Strategy:
/// 1. Disk snapshot diff — snapshot the sbox download directory on disk
///    BEFORE and AFTER calling MountAsync(). New files = our package files.
///    This is the primary approach and works for any newly downloaded package.
/// 2. Direct mounted FS lookup — for already-cached maps, look for
///    maps/{packagename}.vpk in FileSystem.Mounted.
/// 3. Package download cache scan — search the download dir for paths
///    containing the package ident.
/// 4. AssetSystem — for non-map asset packages.
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
			var packageName = GetPackageName( mapIdent );

			// Step 1: Snapshot the sbox download directory on disk BEFORE mounting
			var sboxRoot = GetSboxRoot();
			var downloadDir = string.IsNullOrEmpty( sboxRoot ) ? "" : System.IO.Path.Combine( sboxRoot, "download" );

			var beforeFiles = System.IO.Directory.Exists( downloadDir )
				? SnapshotDiskDirectory( downloadDir )
				: new HashSet<string>();

			Log.Info( $"MapProjectSaver: Disk snapshot before mount: {beforeFiles.Count} file(s) in '{downloadDir}'" );

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

			// Step 4: Snapshot the download directory AFTER mounting and diff
			var afterFiles = System.IO.Directory.Exists( downloadDir )
				? SnapshotDiskDirectory( downloadDir )
				: new HashSet<string>();

			var newFiles = new HashSet<string>( afterFiles );
			newFiles.ExceptWith( beforeFiles );

			Log.Info( $"MapProjectSaver: Disk snapshot after mount: {afterFiles.Count} file(s), new: {newFiles.Count} file(s)" );

			var copiedCount = 0;

			// Approach 1: Copy files discovered by the disk before/after diff
			if ( newFiles.Count > 0 )
			{
				copiedCount = CopyNewDiskFiles( newFiles, downloadDir!, targetRoot );
				if ( copiedCount > 0 )
				{
					Log.Info( $"MapProjectSaver: Saved '{mapIdent}' via disk diff ({copiedCount} file(s))" );
					return copiedCount;
				}
			}

			// Approach 2: Direct lookup in FileSystem.Mounted for already-mounted maps
			copiedCount = CopyViaDirectLookup( packageName, targetRoot );
			if ( copiedCount > 0 )
			{
				Log.Info( $"MapProjectSaver: Saved '{mapIdent}' via direct lookup ({copiedCount} file(s))" );
				return copiedCount;
			}

			// Approach 3: Search the download cache for paths containing our ident
			copiedCount = CopyViaDownloadCacheScan( mapIdent, packageName, downloadDir!, targetRoot );
			if ( copiedCount > 0 )
			{
				Log.Info( $"MapProjectSaver: Saved '{mapIdent}' via download cache scan ({copiedCount} file(s))" );
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
	/// e.g. "thieves.rpdowntown3t" -> "rpdowntown3t"
	/// </summary>
	private static string GetPackageName( string mapIdent )
	{
		var name = mapIdent;
		if ( name.Contains( '.' ) )
			name = name[( name.IndexOf( '.' ) + 1 )..];
		return name;
	}

	/// <summary>
	/// Get the sbox install root directory by using FileSystem.Root.
	/// </summary>
	private static string GetSboxRoot()
	{
		return FileSystem.Root.GetFullPath( "/" ) ?? "";
	}

	/// <summary>
	/// Take a snapshot of all files in a disk directory (recursive).
	/// Returns a set of full file paths.
	/// </summary>
	private static HashSet<string> SnapshotDiskDirectory( string rootDir )
	{
		var files = new HashSet<string>( System.StringComparer.OrdinalIgnoreCase );

		if ( !System.IO.Directory.Exists( rootDir ) )
			return files;

		try
		{
			foreach ( var file in System.IO.Directory.GetFiles( rootDir, "*", System.IO.SearchOption.AllDirectories ) )
			{
				files.Add( file );
			}
		}
		catch ( System.Exception ex )
		{
			Log.Warning( $"MapProjectSaver: Error snapshotting '{rootDir}': {ex.Message}" );
		}

		return files;
	}

	/// <summary>
	/// Copy files discovered by the disk before/after diff.
	/// Maps the source paths to the target directory, stripping the download
	/// root prefix and adjusting for the Maps/ target structure.
	/// </summary>
	private static int CopyNewDiskFiles( HashSet<string> newFiles, string downloadRoot, string targetRoot )
	{
		var copiedCount = 0;
		var downloadRootLength = downloadRoot.Length;

		foreach ( var sourcePath in newFiles )
		{
			// Get the relative path within the download directory
			// e.g. "C:\...\download\assets\maps\rp_downtown_3t.vpk" -> "assets\maps\rp_downtown_3t.vpk"
			var relPath = sourcePath[( downloadRootLength )..].TrimStart( System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar );

			if ( TryCopyFile( sourcePath, relPath, targetRoot ) )
				copiedCount++;
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
	/// Search the download cache directory for paths containing our package ident.
	/// This handles the case where the map was previously downloaded (not newly)
	/// so the disk diff is 0, but the files still exist on disk.
	///
	/// We search for:
	/// - download/assets/{ident_with_underscores}/  (e.g. thieves_rpdowntown3t/)
	/// - download/assets/maps/ — any VPK whose name contains the package name
	/// - Any directory whose name contains the package name
	/// </summary>
	private static int CopyViaDownloadCacheScan( string mapIdent, string packageName, string downloadRoot, string targetRoot )
	{
		if ( string.IsNullOrEmpty( downloadRoot ) || !System.IO.Directory.Exists( downloadRoot ) )
			return 0;

		var copiedCount = 0;
		var assetsDir = System.IO.Path.Combine( downloadRoot, "assets" );

		if ( !System.IO.Directory.Exists( assetsDir ) )
			return 0;

		// Search 1: Package-specific directory (ident with dots -> underscores)
		var identDirName = mapIdent.Replace( '.', '_' );
		var packageDir = System.IO.Path.Combine( assetsDir, identDirName );

		if ( System.IO.Directory.Exists( packageDir ) )
		{
			foreach ( var file in System.IO.Directory.GetFiles( packageDir, "*", System.IO.SearchOption.AllDirectories ) )
			{
				var relPath = file[( packageDir.Length + 1 )..];
				if ( relPath.StartsWith( "thumb", System.StringComparison.OrdinalIgnoreCase ) ) continue;

				if ( TryCopyFile( file, relPath, targetRoot ) )
					copiedCount++;
			}

			if ( copiedCount > 0 )
				return copiedCount;
		}

		// Search 2: Scan top-level directories in assets/ for our package name
		var packageNameLower = packageName.ToLowerInvariant();

		foreach ( var dir in System.IO.Directory.GetDirectories( assetsDir, "*", System.IO.SearchOption.TopDirectoryOnly ) )
		{
			var dirName = System.IO.Path.GetFileName( dir ).ToLowerInvariant();

			// Check if directory name contains the package name or vice versa
			if ( !dirName.Contains( packageNameLower ) && !packageNameLower.Contains( dirName ) )
				continue;

			// Found a matching directory — copy all files
			foreach ( var file in System.IO.Directory.GetFiles( dir, "*", System.IO.SearchOption.AllDirectories ) )
			{
				var relPath = file[( dir.Length + 1 )..];
				if ( relPath.StartsWith( "thumb", System.StringComparison.OrdinalIgnoreCase ) ) continue;

				if ( TryCopyFile( file, relPath, targetRoot ) )
					copiedCount++;
			}

			if ( copiedCount > 0 )
				return copiedCount;
		}

		// Search 3: Scan maps/ subdirectory for VPKs matching our package name
		var mapsDir = System.IO.Path.Combine( assetsDir, "maps" );
		if ( System.IO.Directory.Exists( mapsDir ) )
		{
			foreach ( var dir in System.IO.Directory.GetDirectories( mapsDir, "*", System.IO.SearchOption.TopDirectoryOnly ) )
			{
				var dirName = System.IO.Path.GetFileName( dir ).ToLowerInvariant();
				if ( !dirName.Contains( packageNameLower ) && !packageNameLower.Contains( dirName ) )
					continue;

				foreach ( var file in System.IO.Directory.GetFiles( dir, "*", System.IO.SearchOption.AllDirectories ) )
				{
					var relPath = $"maps/{dirName}/{file[( dir.Length + 1 )..]}";
					if ( TryCopyFile( file, relPath, targetRoot ) )
						copiedCount++;
				}

				if ( copiedCount > 0 )
					return copiedCount;
			}

			// Also check top-level VPKs
			foreach ( var file in System.IO.Directory.GetFiles( mapsDir, "*.vpk", System.IO.SearchOption.TopDirectoryOnly ) )
			{
				var fileName = System.IO.Path.GetFileNameWithoutExtension( file ).ToLowerInvariant();
				if ( !fileName.Contains( packageNameLower ) && !packageNameLower.Contains( fileName ) )
					continue;

				var fullFileName = System.IO.Path.GetFileName( file );
				if ( TryCopyFile( file, $"maps/{fullFileName}", targetRoot ) )
					copiedCount++;
			}
		}

		return copiedCount;
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

		// Normalize path separators
		targetRelative = targetRelative.Replace( '/', System.IO.Path.DirectorySeparatorChar );

		// Strip "maps\" or "maps/" prefix - targetRoot already is Assets/Maps/
		if ( targetRelative.StartsWith( "maps" + System.IO.Path.DirectorySeparatorChar, System.StringComparison.OrdinalIgnoreCase ) ||
			 targetRelative.StartsWith( "maps/", System.StringComparison.OrdinalIgnoreCase ) )
		{
			var prefixLen = targetRelative.StartsWith( "maps/", System.StringComparison.OrdinalIgnoreCase ) ? 5 : 5;
			targetRelative = targetRelative[prefixLen..];
		}

		// Strip "assets\" or "assets/" prefix — download cache paths include this
		if ( targetRelative.StartsWith( "assets" + System.IO.Path.DirectorySeparatorChar, System.StringComparison.OrdinalIgnoreCase ) ||
			 targetRelative.StartsWith( "assets/", System.StringComparison.OrdinalIgnoreCase ) )
		{
			targetRelative = targetRelative[7..];
		}

		var targetPath = System.IO.Path.Combine( targetRoot, targetRelative );
		var targetDir = System.IO.Path.GetDirectoryName( targetPath );

		if ( !string.IsNullOrEmpty( targetDir ) )
		{
			System.IO.Directory.CreateDirectory( targetDir );
		}

		if ( System.IO.File.Exists( targetPath ) )
			return false;

		try
		{
			System.IO.File.Copy( sourcePath, targetPath, overwrite: false );
			return true;
		}
		catch ( System.Exception ex )
		{
			Log.Warning( $"MapProjectSaver: Failed to copy '{sourcePath}' -> '{targetPath}': {ex.Message}" );
			return false;
		}
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
