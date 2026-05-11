[Hide]
[Title( "Money Printer" )]
[Icon( "🖨️" )]
[ClassName( "moneyprinter" )]
[Group( "Building" )]
public class MoneyPrinterTool : ToolMode
{
	public override bool UseSnapGrid => true;
	public override IEnumerable<string> TraceIgnoreTags => ["constraint", "collision"];

	public override string Description => "Spawn a money printer that generates money over time";

	protected override void OnStart()
	{
		base.OnStart();

		RegisterAction( ToolInput.Primary, () => "Place Money Printer", OnPlace );
	}

	void OnPlace()
	{
		var select = TraceSelect();
		if ( !select.IsValid() ) return;

		var placementTrans = GetPlacementTransform( select );
		Spawn( select, placementTrans );
		ShootEffects( select );
	}

	Transform GetPlacementTransform( SelectionPoint select )
	{
		var pos = select.WorldTransform();
		var placementTrans = new Transform( pos.Position );
		placementTrans.Rotation = pos.Rotation;
		return placementTrans;
	}

	public override void OnControl()
	{
		base.OnControl();

		var select = TraceSelect();
		if ( !select.IsValid() ) return;

		var pos = select.WorldTransform();
		var placementTrans = new Transform( pos.Position );
		placementTrans.Rotation = pos.Rotation;

		// Show a preview of the money printer box
		var bounds = new BBox( new Vector3( -12, -12, 0 ), new Vector3( 12, 12, 24 ) );
		DebugOverlay.Box( bounds, Color.Yellow.WithAlpha( 0.6f ), transform: placementTrans );
	}

	[Rpc.Host]
	public void Spawn( SelectionPoint point, Transform tx )
	{
		// Charge the player for placing a printer
		var ownerData = PlayerData.All.FirstOrDefault( p => p.SteamId == Player.SteamId );
		if ( !ownerData.IsValid() || !ownerData.SpendMoney( 2000 ) ) return;

		var go = new GameObject( true, "Money Printer" );
		go.Tags.Add( "removable" );
		go.WorldTransform = tx;

		// Add a box model for the printer body
		var renderer = go.Components.Create<ModelRenderer>();
		renderer.Model = Model.Load( "models/dev/box.vmdl" );
		renderer.Tint = new Color( 1f, 0.84f, 0f ); // Gold

		// Add physics
		var collider = go.Components.Create<BoxCollider>();
		collider.Scale = new Vector3( 24, 24, 24 );
		collider.Center = new Vector3( 0, 0, 12 );

		go.AddComponent<Rigidbody>();

		// Add the money printer entity component
		var printer = go.Components.Create<MoneyPrinterEntity>();
		printer.OwnerSteamId = Player.SteamId;

		if ( !point.IsWorld )
		{
			var joint = go.AddComponent<FixedJoint>();
			joint.Attachment = Joint.AttachmentMode.LocalFrames;
			joint.LocalFrame2 = point.GameObject.WorldTransform.WithScale( 1 ).ToLocal( tx );
			joint.LocalFrame1 = new Transform();
			joint.AngularFrequency = 0;
			joint.LinearFrequency = 0;
			joint.Body = point.GameObject;
			joint.EnableCollision = false;
		}

		ApplyPhysicsProperties( go );

		go.NetworkSpawn( true, null );

		Track( go );

		// undo
		{
			var undo = Player.Undo.Create();
			undo.Name = "Money Printer";
			undo.Icon = "🖨️";
			undo.Add( go );
		}
	}
}
