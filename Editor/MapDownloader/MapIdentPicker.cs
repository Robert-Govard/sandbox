namespace Editor;

/// <summary>
/// A map ident field that looks and behaves like the built-in MapInstance.MapName field.
/// Shows a map icon/preview, asset name, relative path, supports click-to-browse,
/// drag-and-drop, and context menu (copy/paste/clear/find).
/// </summary>
public sealed class MapIdentPicker : Widget
{
	private string _value = "";
	private MapDownloaderComponent _target;
	private Pixmap _pixmap;
	private Pixmap _mapIcon;

	/// <summary>
	/// Current map ident value displayed in the field.
	/// </summary>
	public string Value
	{
		get => _value;
		set
		{
			// Always update — external changes (undo, code) may set same value
			// but we still need to refresh visual state
			var newValue = value ?? "";
			var changed = _value != newValue;
			_value = newValue;
			if ( changed )
			{
				UpdatePixmap();
			}
			Update();
		}
	}

	public MapIdentPicker( Widget parent, MapDownloaderComponent target ) : base( parent )
	{
		_target = target;
		_mapIcon = AssetType.MapFile?.Icon64;
		Cursor = CursorShape.Finger;
		MouseTracking = true;
		AcceptDrops = true;
		IsDraggable = true;
		MinimumHeight = 36;
	}

	[EditorEvent.Frame]
	void Frame()
	{
		var newPixmap = FindAsset()?.GetAssetThumb( true );
		if ( newPixmap != _pixmap )
		{
			_pixmap = newPixmap;
			Update();
		}
	}

	private Asset FindAsset()
	{
		if ( string.IsNullOrEmpty( _value ) ) return null;
		return AssetSystem.FindByPath( _value );
	}

	protected override void OnMouseClick( MouseEvent e )
	{
		base.OnMouseClick( e );

		var picker = AssetPicker.Create( this, AssetType.MapFile );
		picker.Title = "Select Map";
		picker.SetSelection( _value );

		picker.OnAssetPicked += ( assets ) =>
		{
			if ( assets == null || assets.Length == 0 ) return;
			var asset = assets[0];
			if ( asset == null ) return;

			var mapName = ExtractMapName( asset );
			ApplyMapIdent( mapName );
		};

		picker.OnPackagePicked += ( package ) =>
		{
			if ( package == null ) return;

			var ident = package.FullIdent ?? package.Ident ?? "";
			ApplyMapIdent( ident );
		};

		picker.Show();
	}

	protected override void OnContextMenu( ContextMenuEvent e )
	{
		var m = new ContextMenu( this );
		var asset = FindAsset();

		m.AddOption( "Open in Editor", "edit", () => asset?.OpenInEditor() ).Enabled = asset != null && !asset.IsProcedural;
		m.AddOption( "Find in Asset Browser", "search", () => LocalAssetBrowser.OpenTo( asset, true ) ).Enabled = asset is not null;
		m.AddSeparator();
		m.AddOption( "Copy", "file_copy", action: Copy ).Enabled = !string.IsNullOrWhiteSpace( _value );
		m.AddOption( "Paste", "content_paste", action: Paste );
		m.AddSeparator();
		m.AddOption( "Clear", "backspace", action: Clear ).Enabled = !string.IsNullOrEmpty( _value );

		m.OpenAtCursor( false );
		e.Accepted = true;
	}

	private void Copy()
	{
		if ( string.IsNullOrEmpty( _value ) ) return;
		var asset = FindAsset();
		var text = asset != null ? asset.RelativePath : _value;
		EditorUtility.Clipboard.Copy( text );
	}

	private void Paste()
	{
		var path = EditorUtility.Clipboard.Paste();
		if ( string.IsNullOrEmpty( path ) ) return;

		var asset = AssetSystem.FindByPath( path );
		if ( asset != null && asset.AssetType == AssetType.MapFile )
		{
			var mapName = ExtractMapName( asset );
			ApplyMapIdent( mapName );
		}
		else if ( Package.TryParseIdent( path, out _ ) )
		{
			ApplyMapIdent( path );
		}
	}

	private void Clear()
	{
		Value = "";
		_target.MapIdent = "";
		_target.Status = "Cleared.";
	}

	public override void OnDragHover( DragEvent ev )
	{
		if ( ev.Data.Url?.Scheme == "https" )
		{
			ev.Action = DropAction.Link;
			return;
		}

		if ( ev.Data.HasFileOrFolder )
		{
			var asset = AssetSystem.FindByPath( ev.Data.FileOrFolder );
			if ( asset != null && asset.AssetType == AssetType.MapFile )
			{
				ev.Action = DropAction.Link;
				return;
			}
		}
	}

	public override void OnDragDrop( DragEvent ev )
	{
		if ( ev.Data.Url?.Scheme == "https" )
		{
			if ( Package.TryParseIdent( ev.Data.Url.ToString(), out var ident ) )
			{
				var mapIdent = $"{ident.org}.{ident.package}";
				ApplyMapIdent( mapIdent );
			}
			ev.Action = DropAction.Link;
			return;
		}

		if ( ev.Data.HasFileOrFolder )
		{
			var asset = AssetSystem.FindByPath( ev.Data.FileOrFolder );
			if ( asset != null && asset.AssetType == AssetType.MapFile )
			{
				var mapName = ExtractMapName( asset );
				ApplyMapIdent( mapName );
				ev.Action = DropAction.Link;
			}
		}
	}

	protected override void OnDragStart()
	{
		var asset = FindAsset();
		if ( asset == null ) return;

		var drag = new Drag( this );
		drag.Data.Url = new System.Uri( $"file://{asset.AbsolutePath}" );
		drag.Execute();
	}

	private string ExtractMapName( Asset asset )
	{
		var assetPath = asset.RelativePath ?? asset.AbsolutePath ?? "";
		var mapName = assetPath;

		if ( mapName.StartsWith( "maps/" ) ) mapName = mapName[5..];
		if ( mapName.EndsWith( ".vpk" ) ) mapName = mapName[..^4];

		return mapName;
	}

	private void ApplyMapIdent( string ident )
	{
		Value = ident;
		_target.MapIdent = ident;
		_target.Status = $"Selected: {ident}";

		if ( _target.AutoLoad )
		{
			_target.DownloadMap();
		}
	}

	private void UpdatePixmap()
	{
		_pixmap = FindAsset()?.GetAssetThumb( true );
	}

	protected override void OnPaint()
	{
		base.OnPaint();

		var rect = new Rect( 0, Size );
		var asset = FindAsset();
		bool hovered = IsUnderMouse;

		// Icon area (left side)
		var iconRect = rect.Shrink( 2 );
		iconRect.Width = iconRect.Height;

		// Text area (right of icon)
		var textRect = rect;
		textRect.Left = iconRect.Right + 10;

		// Background
		Paint.ClearPen();
		if ( hovered )
		{
			Paint.SetBrush( Color.Lerp( Theme.ControlBackground, Theme.Primary, 0.05f ) );
		}
		else
		{
			Paint.SetBrush( Theme.ControlBackground );
		}
		Paint.DrawRect( rect, Theme.ControlRadius );

		// Icon background
		Paint.ClearPen();
		Paint.SetBrush( Theme.SurfaceBackground.WithAlpha( 0.2f ) );
		Paint.DrawRect( iconRect, 2 );

		if ( asset != null && !asset.IsDeleted )
		{
			// Found asset — show thumbnail
			Paint.Draw( iconRect, asset.GetAssetThumb( true ) );

			// Asset name
			var nameRect = textRect.Shrink( 0, 3 );
			Paint.SetPen( Theme.Text.WithAlpha( 0.9f ) );
			Paint.SetHeadingFont( 8, 450 );
			var t = Paint.DrawText( nameRect, asset.Name, TextFlag.LeftTop );

			// Relative path
			var pathRect = nameRect;
			pathRect.Left = t.Right + 6;
			Paint.SetDefaultFont( 7 );
			Theme.DrawFilename( pathRect, asset.RelativePath, TextFlag.LeftCenter, Theme.Text.WithAlpha( 0.5f ) );
		}
		else if ( !string.IsNullOrWhiteSpace( _value ) )
		{
			// Value set but asset not found — could be a cloud package
			bool isPackage = !_value.Contains( ".vmap" ) && Package.TryParseIdent( _value, out _ );

			if ( isPackage )
			{
				// Cloud package — show map icon
				if ( _mapIcon != null ) Paint.Draw( iconRect, _mapIcon );

				var nameRect = textRect.Shrink( 0, 3 );
				Paint.SetPen( Theme.Text.WithAlpha( 0.9f ) );
				Paint.SetHeadingFont( 8, 450 );

				string displayName;
				if ( Package.TryGetCached( _value, out Package package ) )
				{
					displayName = $"{package.Title} \u2601"; // cloud icon
				}
				else
				{
					displayName = "Cloud Map";
				}

				var t = Paint.DrawText( nameRect, displayName, TextFlag.LeftTop );

				var pathRect = nameRect;
				pathRect.Left = t.Right + 6;
				Paint.SetDefaultFont( 7 );
				Theme.DrawFilename( pathRect, _value, TextFlag.LeftCenter, Theme.Text.WithAlpha( 0.5f ) );
			}
			else
			{
				// Missing asset — red icon
				Paint.SetBrush( Theme.Red.Darken( 0.8f ) );
				Paint.DrawRect( iconRect, 2 );

				Paint.SetPen( Theme.Red );
				Paint.DrawIcon( iconRect, "error", System.Math.Max( 16, iconRect.Height / 2 ) );

				var nameRect = textRect.Shrink( 0, 3 );
				Paint.SetPen( Theme.Text.WithAlpha( 0.9f ) );
				Paint.SetHeadingFont( 8, 450 );
				var t = Paint.DrawText( nameRect, "Missing Map", TextFlag.LeftTop );

				var pathRect = nameRect;
				pathRect.Left = t.Right + 6;
				Paint.SetDefaultFont( 7 );
				Theme.DrawFilename( pathRect, _value, TextFlag.LeftCenter, Theme.Text.WithAlpha( 0.5f ) );
			}
		}
		else
		{
			// Empty — show placeholder
			if ( _mapIcon != null ) Paint.Draw( iconRect, _mapIcon );

			var nameRect = textRect.Shrink( 0, 3 );
			Paint.SetDefaultFont( italic: true );
			Paint.SetPen( Theme.Text.WithAlpha( 0.2f ) );
			Paint.DrawText( nameRect, "Map File", TextFlag.LeftCenter );
		}
	}
}
