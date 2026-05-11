/// <summary>
/// Editor component for downloading maps from the s&amp;box Asset Browser.
/// Add this component to any GameObject to browse, download, and load
/// cloud maps directly from the scene editor inspector.
/// </summary>
[Title( "Map Downloader" )]
[Category( "Map" )]
[Icon( "map" )]
public sealed class MapDownloaderComponent : Component, Component.ExecuteInEditor
{
	/// <summary>
	/// Map package ident to download (e.g. "facepunch.flatgrass").
	/// </summary>
	[Property, Title( "Map Ident" ), Description( "The package ident of the map to download (e.g. facepunch.flatgrass)" )]
	public string MapIdent { get; set; } = "";

	/// <summary>
	/// Whether to automatically load the map into the scene after downloading.
	/// </summary>
	[Property, Title( "Auto Load" ), Description( "Automatically set MapInstance.MapName after downloading" )]
	public bool AutoLoad { get; set; } = true;

	/// <summary>
	/// Whether to save the map and all its dependencies (textures, materials, etc.)
	/// to the project's Assets/Maps directory so the map can be edited in Hammer.
	/// </summary>
	[Property, Title( "Save to Project" ), Description( "Save the map and all dependencies to Assets/Maps/ for Hammer editing" )]
	public bool SaveToProject { get; set; } = true;

	/// <summary>
	/// Status message showing current operation state.
	/// </summary>
	[Property, Title( "Status" ), ReadOnly, WideMode]
	public string Status { get; set; } = "Ready. Click Map Ident to browse maps.";

	/// <summary>
	/// Download and mount the map specified by the MapIdent property.
	/// </summary>
	[Button( "Download Map" )]
	public async void DownloadMap()
	{
		if ( string.IsNullOrWhiteSpace( MapIdent ) )
		{
			Status = "Enter a map ident first (e.g. facepunch.flatgrass).";
			return;
		}

		Status = $"Fetching {MapIdent}...";

		var package = await Package.Fetch( MapIdent, false );

		if ( package == null || package.Revision == null )
		{
			Status = $"Package not found: {MapIdent}";
			return;
		}

		var title = package.Title ?? MapIdent;

		if ( package.TypeName != "map" )
		{
			Status = $"Warning: '{MapIdent}' is a '{package.TypeName}', not a map. Downloading anyway...";
		}

		Status = $"Downloading {title}...";

		await package.MountAsync();

		if ( AutoLoad )
		{
			LoadMapIntoScene( MapIdent );
			Status = $"Downloaded and loaded: {title} ({MapIdent})";
		}
		else
		{
			Status = $"Downloaded: {title} ({MapIdent})";
		}
	}

	/// <summary>
	/// Download the map and immediately switch to it (restarts the game on that map).
	/// </summary>
	[Button( "Download & Play" )]
	public async void DownloadAndPlay()
	{
		if ( string.IsNullOrWhiteSpace( MapIdent ) )
		{
			Status = "Enter a map ident first.";
			return;
		}

		Status = $"Fetching {MapIdent}...";

		var package = await Package.Fetch( MapIdent, false );

		if ( package == null || package.Revision == null )
		{
			Status = $"Package not found: {MapIdent}";
			return;
		}

		var title = package.Title ?? MapIdent;
		Status = $"Downloading {title}...";

		await package.MountAsync();

		Status = $"Switching to {MapIdent}...";

		LaunchArguments.Map = MapIdent;
		Game.Load( Game.Ident, true );
	}

	/// <summary>
	/// List all locally available maps in the mounted filesystem.
	/// </summary>
	[Button( "List Local Maps" )]
	public void ListLocalMaps()
	{
		var localMaps = new List<string>();

		foreach ( var file in FileSystem.Mounted.FindFile( "maps/", "*.vpk", true ) )
		{
			var mapIdent = file;
			if ( mapIdent.StartsWith( "maps/" ) ) mapIdent = mapIdent[5..];
			if ( mapIdent.EndsWith( ".vpk" ) ) mapIdent = mapIdent[..^4];
			localMaps.Add( mapIdent );
		}

		if ( localMaps.Count == 0 )
		{
			Status = "No local maps found.";
		}
		else
		{
			Status = $"Found {localMaps.Count} local map(s): {string.Join( ", ", localMaps.Take( 10 ) )}{(localMaps.Count > 10 ? "..." : "")}";
		}
	}

	/// <summary>
	/// Set the MapInstance component's MapName to the given ident,
	/// which loads the map into the current scene.
	/// </summary>
	private void LoadMapIntoScene( string ident )
	{
		var mapInstance = Scene.GetAllComponents<MapInstance>().FirstOrDefault();
		if ( mapInstance.IsValid() )
		{
			mapInstance.MapName = ident;
		}
	}
}
