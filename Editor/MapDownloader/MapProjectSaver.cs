namespace Editor;

/// <summary>
/// Utility for saving a downloaded map and all its dependencies
/// into the project's Assets/Maps directory so the map can be opened
/// and edited in Hammer.
///
/// Map packages are special in sbox - they're .vpk files, not regular assets.
/// AssetSystem.GetPackageFiles() returns nothing for them, and
/// AssetSystem.CanCloudInstall() returns false. FileSystem.Mounted.GetFullPath()
/// also returns empty for cloud-mounted files.
///
/// The only reliable way to copy them is to search the sbox download cache
/// directory directly on disk after the package has been mounted.
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

			// Step 1: Fetch and mount the package (downloads to cache if needed)
			var package = await Package.FetchAsync( mapIdent, false );
			if ( package == null || package.Revision == null )
			{
				Log.Warning( $"MapProjectSaver: Package not found: '{mapIdent}'" );
				return 0;
			}

			await package.MountAsync();

			// Step 2: Try AssetSystem install for non-map packages
			if ( AssetSystem.CanCloudInstall( package ) && !AssetSystem.IsCloudInstalled( package ) )
			{
				await AssetSystem.InstallAsync( package.FullIdent );
			}

			// Step 3: Copy files - try multiple approaches
			var copiedCount = 0;

			// Approach A: AssetSystem.GetPackageFiles (works for non-map asset packages)
			copiedCount = CopyViaPackageFiles( package, targetRoot );
			if ( copiedCount > 0 )
			{
				Log.Info( $"MapProjectSaver: Saved '{mapIdent}' via PackageFiles ({copiedCount} file(s))" );
				return copiedCount;
			}

			// Approach B: Search the sbox download cache on disk
			// This is the primary method for map packages (.vpk files)
			copiedCount = CopyViaDownloadCache( mapIdent, targetRoot );
			if ( copiedCount > 0 )
			{
				Log.Info( $"MapProjectSaver: Saved '{mapIdent}' via DownloadCache ({copiedCount} file(s))" );
				return copiedCount;
			}

			// Approach C: Try AssetSystem.FindByPath + GetReferences
			copiedCount = CopyViaAssetSystem( mapIdent, targetRoot );
			if ( copiedCount > 0 )
			{
				Log.Info( $"MapProjectSaver: Saved '{mapIdent}' via AssetSystem ({copiedCount} file(s))" );
				return copiedCount;
			}

			// Approach D: Search sbox core/addons directories for built-in maps
			// Built-in maps like "facepunch.flatgrass" are not in the download cache —
			// they ship with s&box inside core/maps/ or addons/base/Assets/maps/
			copiedCount = CopyViaSboxInstallDirs( mapIdent, targetRoot );
			if ( copiedCount > 0 )
			{
				Log.Info( $"MapProjectSaver: Saved '{mapIdent}' via SboxInstallDirs ({copiedCount} file(s))" );
				return copiedCount;
			}

			// Approach E: Use FileSystem.Mounted to read and copy files from VPK
			// Some maps (like facepunch.flatgrass) are packed inside mounted addons
			// and don't exist as standalone files on disk. We can still read them
			// through the virtual filesystem and write them to the project.
			copiedCount = CopyViaMountedFilesystem( mapIdent, targetRoot );
			if ( copiedCount > 0 )
			{
				Log.Info( $"MapProjectSaver: Saved '{mapIdent}' via MountedFilesystem ({copiedCount} file(s))" );
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
	/// Get the sbox install root directory by using FileSystem.Root.
	/// </summary>
	private static string GetSboxRoot()
	{
		// FileSystem.Root maps to the sbox install directory
		return FileSystem.Root.GetFullPath( "/" ) ?? "";
	}

	/// <summary>
	/// Approach A: Use AssetSystem.GetPackageFiles() + FileSystem.Cloud
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
	/// Approach B: Search the sbox download cache directory on disk.
	/// After MountAsync(), the package files are downloaded to:
	///   {sbox_root}/download/assets/maps/{mapname}.{hash}.vpk
	///   {sbox_root}/download/assets/{org}_{package}/
	///
	/// We search both locations and copy everything we find.
	/// This is the most reliable method for map packages.
	/// </summary>
	private static int CopyViaDownloadCache( string mapIdent, string targetRoot )
	{
		var sboxRoot = GetSboxRoot();
		Log.Info( $"MapProjectSaver: sboxRoot = '{sboxRoot}'" );

		if ( string.IsNullOrEmpty( sboxRoot ) || !System.IO.Directory.Exists( sboxRoot ) )
		{
			Log.Warning( "MapProjectSaver: Could not find sbox root directory" );
			return 0;
		}

		var downloadAssets = System.IO.Path.Combine( sboxRoot, "download", "assets" );
		Log.Info( $"MapProjectSaver: downloadAssets = '{downloadAssets}' exists={System.IO.Directory.Exists( downloadAssets )}" );

		if ( !System.IO.Directory.Exists( downloadAssets ) )
		{
			Log.Warning( $"MapProjectSaver: Download cache not found at '{downloadAssets}'" );
			return 0;
		}

		var identName = mapIdent;
		if ( identName.Contains( '/' ) )
			identName = identName.Split( '/' )[^1];

		// Package ident format is "org.package" — the .vpk file uses just the package part
		// e.g. "hillrop.ttt_depot_fof" -> .vpk is "ttt_depot_fof.hash.vpk"
		var packageName = identName;
		if ( packageName.Contains( '.' ) )
			packageName = packageName[( packageName.IndexOf( '.' ) + 1 )..];

		// Convert ident to directory name format (dots become underscores)
		// e.g. "coconutcompany.hub" -> "coconutcompany_hub"
		var identDirName = mapIdent.Replace( '.', '_' );

		// Build a list of search tokens derived from the package name.
		// Map VPK names often differ from the ident: e.g. "thieves.rpdowntown3t"
		// has VPK files named "rp_downtown_3t_v1" and lives in a "downtown" subdirectory.
		var searchTokens = BuildSearchTokens( packageName );

		Log.Info( $"MapProjectSaver: identName = '{identName}', packageName = '{packageName}', identDirName = '{identDirName}', tokens = [{string.Join( ", ", searchTokens )}]" );

		var copiedCount = 0;

		// Search 1: Find .vpk files in download/assets/maps/ matching any search token
		var mapsDir = System.IO.Path.Combine( downloadAssets, "maps" );
		if ( System.IO.Directory.Exists( mapsDir ) )
		{
			// Search top-level VPKs
			foreach ( var file in System.IO.Directory.GetFiles( mapsDir, "*.vpk", System.IO.SearchOption.TopDirectoryOnly ) )
			{
				var fileName = System.IO.Path.GetFileNameWithoutExtension( file ).ToLowerInvariant();
				if ( !searchTokens.Any( t => fileName.Contains( t ) ) ) continue;

				var fullFileName = System.IO.Path.GetFileName( file );
				if ( TryCopyFile( file, $"maps/{fullFileName}", targetRoot ) )
					copiedCount++;
			}

			// Search subdirectories — match if dir name contains any token OR if any VPK inside matches
			foreach ( var dir in System.IO.Directory.GetDirectories( mapsDir ) )
			{
				var dirName = System.IO.Path.GetFileName( dir );
				var dirNameLower = dirName.ToLowerInvariant();
				var dirMatches = searchTokens.Any( t => dirNameLower.Contains( t ) );

				// If directory name matches, copy ALL VPKs inside
				if ( dirMatches )
				{
					foreach ( var file in System.IO.Directory.GetFiles( dir, "*.vpk", System.IO.SearchOption.AllDirectories ) )
					{
						var relPath = $"maps/{dirName}/{file[( dir.Length + 1 )..]}";
						if ( TryCopyFile( file, relPath, targetRoot ) )
							copiedCount++;
					}

					// Also copy non-VPK dependencies (materials, models, etc.) from this directory
					foreach ( var file in System.IO.Directory.GetFiles( dir, "*", System.IO.SearchOption.AllDirectories ) )
					{
						var ext = System.IO.Path.GetExtension( file ).ToLowerInvariant();
						if ( ext == ".vpk" ) continue; // already handled

						var relPath = $"maps/{dirName}/{file[( dir.Length + 1 )..]}";
						if ( TryCopyFile( file, relPath, targetRoot ) )
							copiedCount++;
					}
				}
				else
				{
					// Directory name doesn't match — check if any VPK inside matches
					foreach ( var file in System.IO.Directory.GetFiles( dir, "*.vpk", System.IO.SearchOption.AllDirectories ) )
					{
						var fileName = System.IO.Path.GetFileNameWithoutExtension( file ).ToLowerInvariant();
						if ( !searchTokens.Any( t => fileName.Contains( t ) ) ) continue;

						// Found a matching VPK — copy the entire directory
						foreach ( var allFile in System.IO.Directory.GetFiles( dir, "*", System.IO.SearchOption.AllDirectories ) )
						{
							var relPath = $"maps/{dirName}/{allFile[( dir.Length + 1 )..]}";
							if ( TryCopyFile( allFile, relPath, targetRoot ) )
								copiedCount++;
						}

						break; // Already copied the whole directory
					}
				}
			}
		}

		// Search 2: Copy files from the package-specific directory
		// e.g. download/assets/coconutcompany_hub/
		var packageDir = System.IO.Path.Combine( downloadAssets, identDirName );
		if ( System.IO.Directory.Exists( packageDir ) )
		{
			foreach ( var file in System.IO.Directory.GetFiles( packageDir, "*", System.IO.SearchOption.AllDirectories ) )
			{
				var relPath = file[( packageDir.Length + 1 )..];
				// Skip thumbnails — they're not needed for the map to work
				if ( relPath.StartsWith( "thumb", System.StringComparison.OrdinalIgnoreCase ) ) continue;

				if ( TryCopyFile( file, relPath, targetRoot ) )
					copiedCount++;
			}
		}

		return copiedCount;
	}

	/// <summary>
	/// Approach C: Try to find the asset via AssetSystem.FindByPath
	/// and copy it with references (textures, materials, etc.)
	/// </summary>
	private static int CopyViaAssetSystem( string mapIdent, string targetRoot )
	{
		var candidates = new List<string>();

		if ( !mapIdent.StartsWith( "maps/" ) )
			candidates.Add( $"maps/{mapIdent}.vpk" );

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
	/// Approach D: Search sbox core/addons directories for built-in maps.
	/// Maps like "facepunch.flatgrass" ship with s&box and are not in the
	/// download cache. They live in:
	///   {sbox_root}/core/maps/{mapname}.vpk
	///   {sbox_root}/addons/base/Assets/maps/{mapname}.vpk
	///   {sbox_root}/addons/citizen/assets/maps/{mapname}.vpk
	/// We also search subdirectories (some maps have deps alongside the VPK).
	/// </summary>
	private static int CopyViaSboxInstallDirs( string mapIdent, string targetRoot )
	{
		var sboxRoot = GetSboxRoot();
		if ( string.IsNullOrEmpty( sboxRoot ) || !System.IO.Directory.Exists( sboxRoot ) )
			return 0;

		// Extract the package name part (after the dot)
		var packageName = mapIdent;
		if ( packageName.Contains( '.' ) )
			packageName = packageName[( packageName.IndexOf( '.' ) + 1 )..];

		// Build search tokens (same logic as CopyViaDownloadCache)
		var searchTokens = BuildSearchTokens( packageName );

		// Directories to search for built-in maps
		var searchDirs = new[]
		{
			System.IO.Path.Combine( sboxRoot, "core", "maps" ),
			System.IO.Path.Combine( sboxRoot, "addons", "base", "Assets", "maps" ),
			System.IO.Path.Combine( sboxRoot, "addons", "citizen", "assets", "maps" ),
		};

		var copiedCount = 0;

		foreach ( var searchDir in searchDirs )
		{
			if ( !System.IO.Directory.Exists( searchDir ) ) continue;

			// Search top-level VPKs
			foreach ( var file in System.IO.Directory.GetFiles( searchDir, "*.vpk", System.IO.SearchOption.TopDirectoryOnly ) )
			{
				var fileName = System.IO.Path.GetFileNameWithoutExtension( file ).ToLowerInvariant();
				if ( !searchTokens.Any( t => fileName.Contains( t ) ) ) continue;

				var fullFileName = System.IO.Path.GetFileName( file );
				if ( TryCopyFile( file, $"maps/{fullFileName}", targetRoot ) )
					copiedCount++;
			}

			// Search subdirectories matching tokens
			foreach ( var dir in System.IO.Directory.GetDirectories( searchDir ) )
			{
				var dirName = System.IO.Path.GetFileName( dir );
				var dirNameLower = dirName.ToLowerInvariant();

				// Check if directory name or any VPK inside matches
				var dirMatches = searchTokens.Any( t => dirNameLower.Contains( t ) );
				if ( !dirMatches )
				{
					// Check VPKs inside
					var hasMatch = false;
					foreach ( var vpkFile in System.IO.Directory.GetFiles( dir, "*.vpk", System.IO.SearchOption.TopDirectoryOnly ) )
					{
						var vpkName = System.IO.Path.GetFileNameWithoutExtension( vpkFile ).ToLowerInvariant();
						if ( searchTokens.Any( t => vpkName.Contains( t ) ) )
						{
							hasMatch = true;
							break;
						}
					}
					if ( !hasMatch ) continue;
				}

				// Copy everything from this matching directory
				foreach ( var file in System.IO.Directory.GetFiles( dir, "*", System.IO.SearchOption.AllDirectories ) )
				{
					var relPath = $"maps/{dirName}/{file[( dir.Length + 1 )..]}";
					if ( TryCopyFile( file, relPath, targetRoot ) )
						copiedCount++;
				}
			}
		}

		return copiedCount;
	}

	/// <summary>
	/// Approach E: Use FileSystem.Mounted to read and copy files from VPK.
	/// Maps like "facepunch.flatgrass" are packed inside mounted addon VPKs
	/// and don't exist as standalone files on disk. After MountAsync(), they're
	/// accessible through the virtual filesystem.
	///
	/// We enumerate ALL files in maps/ matching the map name, then also scan
	/// for dependency directories (materials, models, sounds, etc.) that sit
	/// alongside the map VPK.
	/// </summary>
	private static int CopyViaMountedFilesystem( string mapIdent, string targetRoot )
	{
		// Extract the package name part (after the dot)
		var packageName = mapIdent;
		if ( packageName.Contains( '.' ) )
			packageName = packageName[( packageName.IndexOf( '.' ) + 1 )..];

		// Build search tokens for fuzzy matching
		var searchTokens = BuildSearchTokens( packageName );

		var copiedCount = 0;

		// Phase 1: Find and copy VPK files in maps/ matching the map name
		// FileSystem.Mounted.FindFile returns file names without the root path
		foreach ( var file in FileSystem.Mounted.FindFile( "maps/", "*.vpk", true ) )
		{
			var fileNameLower = file.ToLowerInvariant();

			// Match if any search token is found in the filename
			// e.g. "flatgrass" matches "flatgrass.vpk", "flatgrass_bakeresourcecache.vpk"
			if ( !searchTokens.Any( t => fileNameLower.Contains( t ) ) ) continue;

			var mountedPath = $"maps/{file}";
			if ( !FileSystem.Mounted.FileExists( mountedPath ) ) continue;

			var data = FileSystem.Mounted.ReadAllBytes( mountedPath );
			if ( data.Length == 0 ) continue;

			var targetRelative = file;
			// Strip leading "maps/" if present (FindFile may return "subdir/file.vpk")
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
				Log.Info( $"MapProjectSaver: Extracted '{mountedPath}' from mounted FS ({data.Length} bytes)" );
			}
		}

		// Phase 2: Copy dependency directories that sit alongside the map.
		// When a map is mounted, its dependencies (materials, models, sounds, etc.)
		// may appear in subdirectories of maps/ named after the map.
		// e.g. maps/flatgrass/materials/..., maps/flatgrass/models/...
		var depSearchNames = new[] { mapIdent, packageName };
		foreach ( var searchName in depSearchNames )
		{
			var mapDepDir = $"maps/{searchName}";
			if ( !FileSystem.Mounted.DirectoryExists( mapDepDir ) ) continue;

			copiedCount += CopyMountedDirectoryRecursive( mapDepDir, targetRoot, copiedCount );
			break; // Found and copied, no need to try other names
		}

		return copiedCount;
	}

	/// <summary>
	/// Recursively copy all files from a mounted filesystem directory to the target.
	/// Strips the "maps/" prefix from the relative path since targetRoot is already Assets/Maps/.
	/// </summary>
	private static int CopyMountedDirectoryRecursive( string mountedDir, string targetRoot, int existingCount )
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
	/// Build a list of fuzzy search tokens from a package name.
	/// Used by multiple approaches to find map files whose names
	/// differ from the ident (e.g. "rpdowntown3t" vs "rp_downtown_3t_v1").
	/// </summary>
	private static List<string> BuildSearchTokens( string packageName )
	{
		var tokens = new List<string> { packageName.ToLowerInvariant() };

		// Add the name with underscores stripped
		var bareName = packageName.Replace( "_", "" ).ToLowerInvariant();
		if ( bareName != packageName.ToLowerInvariant() )
			tokens.Add( bareName );

		// Split on underscores, dots, spaces and add each non-trivial segment
		foreach ( var segment in packageName.Split( '_', '.', ' ' ) )
		{
			if ( segment.Length >= 3 )
				tokens.Add( segment.ToLowerInvariant() );
		}

		return tokens;
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
