[Icon( "👻" )]
[Title( "Possess" )]
[ClassName( "possess" )]
[Group( "Tools" )]
public class PossessTool : ToolMode
{
	public override string Description => "#tool.hint.possess.description";

	[Property, Sync, Range( 50, 1000 ), Title( "Speed" )]
	public float MaxSpeed { get; set; } = 200f;

	[Property, Sync, Range( 0, 800 ), Title( "Jump Power" )]
	public float JumpPower { get; set; } = 300f;

	protected override void OnStart()
	{
		base.OnStart();

		RegisterAction( ToolInput.Primary, () => "#tool.hint.possess.enter", OnPossess );
		RegisterAction( ToolInput.Secondary, () => "#tool.hint.possess.exit", OnExit );
	}

	void OnPossess()
	{
		var select = TraceSelect();
		if ( !select.IsValid() ) return;

		// Can't possess the world
		if ( select.IsWorld ) return;
		// Can't possess players
		if ( select.IsPlayer ) return;

		var target = select.GameObject?.Root;
		if ( !target.IsValid() ) return;

		// Must have physics to be controllable
		var body = target.GetComponent<Rigidbody>();
		if ( !body.IsValid() )
		{
			ShootFailEffects( select );
			return;
		}

		// Already possessed by someone?
		if ( target.GetComponent<PropPossession>() is { IsPossessed: true } )
		{
			ShootFailEffects( select );
			return;
		}

		Possess( target );
		ShootEffects( select );
	}

	void OnExit()
	{
		// Find any prop possessed by this player and exit it
		var player = Player;
		if ( !player.IsValid() ) return;

		var possessions = Scene.GetAllComponents<PropPossession>()
			.Where( p => p.IsPossessed && p.PossessingPlayer == player );

		foreach ( var possession in possessions )
		{
			if ( possession.IsValid() )
			{
				ExitPossession( possession.GameObject );
				break; // Only exit one at a time
			}
		}
	}

	/// <summary>
	/// When possessing, trace from the camera position instead of the player's eyes.
	/// This allows the player to aim at things near the possessed prop.
	/// </summary>
	public new SelectionPoint TraceSelect()
	{
		var player = Player;
		if ( !player.IsValid() ) return default;

		// If we're currently possessing something, trace from the camera
		var possession = Scene.GetAllComponents<PropPossession>()
			.FirstOrDefault( p => p.IsPossessed && p.PossessingPlayer == player );

		if ( possession.IsValid() )
		{
			var camera = Scene.Camera;
			if ( camera.IsValid() )
			{
				var ray = new Ray( camera.WorldPosition, camera.WorldRotation.Forward );
				return TraceFromRay( ray, 4096, player.GameObject );
			}
		}

		// Default: trace from player's eyes
		return base.TraceSelect();
	}

	[Rpc.Host]
	public void Possess( GameObject target )
	{
		if ( !target.IsValid() ) return;

		var player = Player;
		if ( !player.IsValid() ) return;

		// Add the possession component to the prop
		var possession = target.GetOrAddComponent<PropPossession>();
		possession.MaxSpeed = MaxSpeed;
		possession.JumpForce = JumpPower;
		possession.Possess( player );

		// Hide the player's body (but keep the controller enabled so input still flows)
		if ( player.Body.IsValid() )
			player.Body.Enabled = false;

		// Switch to third person so the camera detaches from first person view
		player.Controller.ThirdPerson = true;

		// Add the camera controller to the player's GameObject (so it runs on the player's client)
		var camController = player.GameObject.GetOrAddComponent<PossessionCameraController>();
		camController.PossessedProp = target;

		player.GameObject.Network.Refresh();

		player.PlayerData?.AddStat( "tool.possess.enter" );
	}

	[Rpc.Host]
	public void ExitPossession( GameObject target )
	{
		if ( !target.IsValid() ) return;

		var player = Player;
		if ( !player.IsValid() ) return;

		var possession = target.GetComponent<PropPossession>();
		if ( !possession.IsValid() ) return;

		// Only the possessing player can exit
		if ( possession.PossessingPlayer != player ) return;

		// Restore the player's body
		if ( player.Body.IsValid() )
			player.Body.Enabled = true;

		player.Controller.ThirdPerson = false;

		// Remove camera controller from the player
		var camController = player.GameObject.GetComponent<PossessionCameraController>();
		if ( camController.IsValid() )
			camController.Destroy();

		player.GameObject.Network.Refresh();

		// Depossess the prop
		possession.Depossess();

		player.PlayerData?.AddStat( "tool.possess.exit" );
	}

	public override void OnControl()
	{
		base.OnControl();

		// If we're currently possessing something, show a visual link
		var player = Player;
		if ( !player.IsValid() ) return;

		var possession = Scene.GetAllComponents<PropPossession>()
			.FirstOrDefault( p => p.IsPossessed && p.PossessingPlayer == player );

		if ( possession.IsValid() && possession.GameObject.IsValid() )
		{
			DebugOverlay.Line( player.WorldPosition, possession.GameObject.WorldPosition, Color.Cyan, 5.0f );
		}
	}

	/// <summary>
	/// Override beam origin when possessing — draw the beam from the camera
	/// instead of the player's muzzle (which is far away at the frozen position).
	/// </summary>
	public override void ShootEffects( SelectionPoint target )
	{
		if ( !Toolgun.IsValid() ) return;

		var player = Toolgun.Owner;
		if ( !player.IsValid() ) return;

		if ( !target.IsValid() ) return;

		Toolgun.SpinCoil();

		// When possessing, the beam should start from the camera, not the muzzle
		var possession = Scene.GetAllComponents<PropPossession>()
			.FirstOrDefault( p => p.IsPossessed && p.PossessingPlayer == player );

		var beamOrigin = possession.IsValid()
			? Scene.Camera?.WorldPosition ?? Toolgun.MuzzleTransform.WorldTransform.Position
			: Toolgun.MuzzleTransform.WorldTransform.Position;

		if ( Toolgun.SuccessImpactEffect is GameObject impactPrefab )
		{
			var wt = target.WorldTransform();
			wt.Rotation = wt.Rotation * new Angles( 90, 0, 0 );

			var impact = impactPrefab.Clone( wt, null, false );
			impact.Enabled = true;
		}

		if ( Toolgun.SuccessBeamEffect is GameObject beamEffect )
		{
			var wt = target.WorldTransform();

			var go = beamEffect.Clone( new Transform( beamOrigin ), null, false );

			foreach ( var beam in go.GetComponentsInChildren<BeamEffect>( true ) )
			{
				beam.TargetPosition = wt.Position;
			}

			go.Enabled = true;
		}

		Toolgun.ViewModel?.GetComponentInChildren<SkinnedModelRenderer>().Set( "b_attack", true );
	}
}
