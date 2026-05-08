/// <summary>
/// Added to the player's GameObject when they possess a prop.
/// Handles the third-person orbital camera that follows the possessed prop.
/// Uses OnPreRender (like FreeCam) so it works independently of camera events.
/// Freezes the player at their original position and handles exit input.
/// </summary>
public sealed class PossessionCameraController : Component
{
	/// <summary>
	/// The possessed prop's GameObject — the camera orbits around this.
	/// </summary>
	[Property] public GameObject PossessedProp { get; set; }

	[Property, Group( "Camera" )] public float CameraDistance { get; set; } = 200f;
	[Property, Group( "Camera" )] public float CameraHeight { get; set; } = 40f;
	[Property, Group( "Camera" )] public float CameraSmoothing { get; set; } = 5f;
	[Property, Group( "Camera" )] public float MinCameraDistance { get; set; } = 80f;

	private float _smoothedDistance;
	private bool _initialized;
	private Vector3 _frozenPosition;

	protected override void OnStart()
	{
		// Save the player's position so we can freeze them here
		var player = GetComponentInParent<Player>();
		if ( player.IsValid() )
		{
			_frozenPosition = player.WorldPosition;
		}
	}

	protected override void OnUpdate()
	{
		if ( IsProxy ) return;

		var player = GetComponentInParent<Player>();
		if ( !player.IsValid() ) return;

		// Auto-exit if the prop was destroyed or is no longer valid
		if ( !PossessedProp.IsValid() )
		{
			ForceExit( player );
			return;
		}

		// Also auto-exit if the possession component is gone (prop deleted, etc.)
		var possession = PossessedProp.GetComponent<PropPossession>();
		if ( !possession.IsValid() || !possession.IsPossessed )
		{
			ForceExit( player );
			return;
		}

		// Freeze the player at their original position so they don't walk around
		// and their collider doesn't interfere with the possessed prop's physics
		player.WorldPosition = _frozenPosition;

		// Exit on R key — send RPC to host
		if ( Input.Pressed( "reload" ) )
		{
			RequestExit();
		}
	}

	protected override void OnPreRender()
	{
		if ( IsProxy ) return;
		if ( !PossessedProp.IsValid() ) return;

		var camera = Scene.Camera;
		if ( camera is null ) return;

		var player = GetComponentInParent<Player>();
		if ( !player.IsValid() ) return;

		var propPos = PossessedProp.WorldPosition + Vector3.Up * CameraHeight;

		if ( !_initialized )
		{
			_initialized = true;

			// Calculate minimum distance based on prop bounds
			var bounds = PossessedProp.GetBounds();
			var propSize = bounds.Size.Length;
			_smoothedDistance = MathF.Max( CameraDistance, propSize + MinCameraDistance );
		}

		// Use the player's eye angles for camera direction — they're kept in sync
		// with mouse input by the PlayerController (which stays enabled during possession)
		var eyeAngles = player.Controller?.EyeAngles ?? Angles.Zero;
		var camRot = eyeAngles.ToRotation();

		// Smooth orbit distance
		_smoothedDistance = _smoothedDistance.LerpTo( CameraDistance, Time.Delta * CameraSmoothing );

		var desiredPos = propPos + camRot.Backward * _smoothedDistance;

		// Trace to prevent camera going through walls
		var tr = Scene.Trace.FromTo( propPos, desiredPos )
			.Radius( 8f )
			.WithTag( "world" )
			.IgnoreGameObjectHierarchy( PossessedProp.Root )
			.Run();

		var camPos = tr.Hit ? tr.HitPosition + ( propPos - desiredPos ).Normal * 4f : desiredPos;

		camera.WorldPosition = camPos;
		camera.WorldRotation = Rotation.LookAt( propPos - camPos, Vector3.Up );

		// Hide first-person rendering (the player body, viewmodel, etc.)
		camera.RenderExcludeTags.Add( "firstperson" );
	}

	/// <summary>
	/// Client requests exit from possession. Sends RPC to host.
	/// </summary>
	[Rpc.Host]
	private void RequestExit()
	{
		var player = GetComponentInParent<Player>();
		if ( !player.IsValid() ) return;

		// Find the prop this player is possessing
		var possession = Scene.GetAllComponents<PropPossession>()
			.FirstOrDefault( p => p.IsPossessed && p.PossessingPlayer == player );

		if ( possession.IsValid() )
		{
			RestorePlayer( player );
			Destroy();

			player.GameObject.Network.Refresh();

			// Depossess the prop
			possession.Depossess();

			player.PlayerData?.AddStat( "tool.possess.exit" );
		}
	}

	/// <summary>
	/// Force exit possession — called when the prop is destroyed or possession is lost.
	/// Runs on the client, sends RPC to host.
	/// </summary>
	[Rpc.Host]
	private void ForceExit( Player player )
	{
		if ( !player.IsValid() ) return;

		// Find any possession by this player
		var possession = Scene.GetAllComponents<PropPossession>()
			.FirstOrDefault( p => p.IsPossessed && p.PossessingPlayer == player );

		if ( possession.IsValid() )
			possession.Depossess();

		RestorePlayer( player );
		Destroy();

		player.GameObject.Network.Refresh();

		player.PlayerData?.AddStat( "tool.possess.exit" );
	}

	/// <summary>
	/// Restores the player to normal state after possession ends.
	/// </summary>
	private void RestorePlayer( Player player )
	{
		if ( !player.IsValid() ) return;

		player.Controller.ThirdPerson = false;
		if ( player.Body.IsValid() )
			player.Body.Enabled = true;
	}

	protected override void OnDestroy()
	{
		if ( IsProxy ) return;

		// Restore camera exclusions when the controller is removed
		var camera = Scene.Camera;
		if ( camera is not null )
		{
			camera.RenderExcludeTags.Remove( "firstperson" );
		}
	}
}
