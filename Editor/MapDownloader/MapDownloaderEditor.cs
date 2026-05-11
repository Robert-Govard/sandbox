namespace Editor;

/// <summary>
/// Custom editor widget for MapDownloaderComponent.
/// Replaces the default component inspector with a rich UI that includes
/// a map ident picker (like MapInstance.MapName), download buttons,
/// and save-to-project functionality.
/// </summary>
[CustomEditor( typeof( MapDownloaderComponent ) )]
public sealed class MapDownloaderEditor : ComponentEditorWidget
{
	private MapDownloaderComponent Target { get; set; }
	private MapIdentPicker IdentPicker { get; set; }
	private Editor.Label StatusLabel { get; set; }
	private Editor.ControlSheet MainSheet { get; set; }

	private string _lastSavedIdent = "";
	private string _lastDownloadStatus = "";
	private bool _isSaving;

	public MapDownloaderEditor( SerializedObject obj ) : base( obj )
	{
		Target = obj.Targets.First() as MapDownloaderComponent;

		SetSizeMode( SizeMode.Default, SizeMode.Default );

		Layout = Layout.Column();
		Layout.Margin = 4;
		Layout.Spacing = 8;

		// Map Ident picker — looks like MapInstance.MapName field
		IdentPicker = Layout.Add( new MapIdentPicker( this, Target ) );
		IdentPicker.Value = Target.MapIdent ?? "";

		// Action buttons row 1
		var btnRow1 = Layout.Add( new Widget( this ) );
		btnRow1.Layout = Layout.Row();
		btnRow1.Layout.Spacing = 4;

		var downloadBtn = btnRow1.Layout.Add( new Editor.Button( "Download" ) );
		downloadBtn.SetStyles( "flex-grow: 1;" );
		downloadBtn.Clicked += () =>
		{
			Target.MapIdent = IdentPicker.Value;
			_lastDownloadStatus = "";
			Target.DownloadMap();
		};

		var playBtn = btnRow1.Layout.Add( new Editor.Button.Primary( "Download & Play" ) );
		playBtn.SetStyles( "flex-grow: 1;" );
		playBtn.Clicked += () =>
		{
			Target.MapIdent = IdentPicker.Value;
			Target.DownloadAndPlay();
		};

		// Action buttons row 2
		var btnRow2 = Layout.Add( new Widget( this ) );
		btnRow2.Layout = Layout.Row();
		btnRow2.Layout.Spacing = 4;

		var saveBtn = btnRow2.Layout.Add( new Editor.Button.Primary( "Save to Project" ) );
		saveBtn.SetStyles( "flex-grow: 1;" );
		saveBtn.Clicked += () =>
		{
			SaveCurrentMap();
		};

		var listBtn = btnRow2.Layout.Add( new Editor.Button( "Local Maps" ) );
		listBtn.SetStyles( "flex-grow: 1;" );
		listBtn.Clicked += () =>
		{
			Target.ListLocalMaps();
		};

		// Action buttons row 3
		var btnRow3 = Layout.Add( new Widget( this ) );
		btnRow3.Layout = Layout.Row();
		btnRow3.Layout.Spacing = 4;

		var openFolderBtn = btnRow3.Layout.Add( new Editor.Button( "Open Maps Folder" ) );
		openFolderBtn.SetStyles( "flex-grow: 1;" );
		openFolderBtn.Clicked += () =>
		{
			var mapsDir = MapProjectSaver.GetMapsDirectory();
			EditorUtility.OpenFolder( mapsDir );
		};

		// Status
		StatusLabel = Layout.Add( new Editor.Label( Target.Status ?? "Ready", this ) );
		StatusLabel.SetStyles( "color: #8cb4ff; padding: 4px; background-color: #1a1a25; border-radius: 4px; font-size: 12px;" );

		// Property sheet for AutoLoad and SaveToProject only
		MainSheet = new Editor.ControlSheet();
		Layout.Add( MainSheet );

		RebuildSheet();
	}

	[EditorEvent.Hotload]
	void RebuildSheet()
	{
		MainSheet.Clear( true );
		var so = Target?.GetSerialized();
		if ( so == null ) return;

		MainSheet.AddObject( so, p => p.DisplayName is "Auto Load" or "Save to Project" or "Status" );
	}

	public override void Update()
	{
		base.Update();

		if ( Target.IsValid() )
		{
			StatusLabel.Text = Target.Status ?? "";

			// Sync picker value from target
			if ( IdentPicker.Value != (Target.MapIdent ?? "") )
			{
				IdentPicker.Value = Target.MapIdent ?? "";
			}

			// Auto-save: detect when a download completes
			if ( Target.SaveToProject && !string.IsNullOrEmpty( Target.MapIdent ) )
			{
				var status = Target.Status ?? "";
				bool isDownloaded = status.StartsWith( "Downloaded" );
				bool isNewDownload = status != _lastDownloadStatus && isDownloaded;

				if ( isNewDownload && Target.MapIdent != _lastSavedIdent )
				{
					_lastDownloadStatus = status;
					_lastSavedIdent = Target.MapIdent;
					SaveCurrentMap();
				}
			}
		}
	}

	private async void SaveCurrentMap()
	{
		if ( _isSaving ) return;
		if ( string.IsNullOrWhiteSpace( Target.MapIdent ) )
		{
			Target.Status = "No map ident to save.";
			return;
		}

		_isSaving = true;
		Target.Status = $"Saving {Target.MapIdent} to project...";

		try
		{
			var count = await MapProjectSaver.SaveWithDependencies( Target.MapIdent );

			if ( !Target.IsValid() ) return;

			if ( count > 0 )
			{
				Target.Status = $"Saved {count} file(s) to Assets/Maps/ | {Target.MapIdent}";
			}
			else
			{
				Target.Status = $"No new files to save | {Target.MapIdent}";
			}
		}
		finally
		{
			_isSaving = false;
		}
	}
}
