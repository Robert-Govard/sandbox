/// <summary>
/// Added to a prop when a player possesses it. Implements IPlayerControllable
/// so the ControlSystem feeds input to it. Handles movement (roll/push),
/// jumping, and visual feedback while possessed.
/// Exit is handled by PossessionCameraController on the client side.
/// </summary>
public sealed class PropPossession : Component, IPlayerControllable, Component.IDamageable
{
	[Property, Group( "Movement" )] public float MaxSpeed { get; set; } = 200f;
	[Property, Group( "Movement" )] public float JumpForce { get; set; } = 300f;

	/// <summary>
	/// The player who is currently possessing this prop.
	/// </summary>
	[Sync] public Player PossessingPlayer { get; set; }

	/// <summary>
	/// Whether this prop is currently being possessed by a player.
	/// </summary>
	public bool IsPossessed => PossessingPlayer.IsValid();

	private Rigidbody _body;
	private TimeSince _timeSinceLastJump;
	private Color _originalTint = Color.White;
	private bool _storedOriginalTint;

	protected override void OnStart()
	{
		_body = GetComponent<Rigidbody>();
	}

	protected override void OnUpdate()
	{
		if ( !IsPossessed ) return;

		UpdateVisualFeedback();

		// If the possessing player is dead or gone, depossess
		if ( !PossessingPlayer.IsValid() || PossessingPlayer.Health < 1 )
		{
			Depossess();
		}
	}

	protected override void OnDisabled()
	{
		RestoreVisuals();
	}

	private void UpdateVisualFeedback()
	{
		var prop = GetComponent<Prop>();
		if ( !prop.IsValid() ) return;

		// Pulsing tint on the possessed prop
		var pulse = MathF.Sin( Time.Now * 4f ) * 0.15f + 0.85f;
		prop.Tint = Color.Lerp( _originalTint, Color.Cyan, 0.3f ) * pulse;
	}

	private void RestoreVisuals()
	{
		if ( !_storedOriginalTint ) return;

		var prop = GetComponent<Prop>();
		if ( prop.IsValid() )
		{
			prop.Tint = _originalTint;
		}
		_storedOriginalTint = false;
	}

	/// <summary>
	/// Called to start possessing this prop. Stores the original tint.
	/// </summary>
	public void Possess( Player player )
	{
		PossessingPlayer = player;

		var prop = GetComponent<Prop>();
		if ( prop.IsValid() )
		{
			_originalTint = prop.Tint;
			_storedOriginalTint = true;
		}

		// Make sure physics is enabled
		if ( _body.IsValid() )
		{
			_body.MotionEnabled = true;
		}
	}

	/// <summary>
	/// Called to stop possessing this prop. Restores visuals and cleans up.
	/// </summary>
	public void Depossess()
	{
		RestoreVisuals();
		PossessingPlayer = null;
	}

	bool IPlayerControllable.CanControl( Player player )
	{
		return IsPossessed && PossessingPlayer == player;
	}

	void IPlayerControllable.OnStartControl() { }
	void IPlayerControllable.OnEndControl() { }

	void IPlayerControllable.OnControl()
	{
		if ( !IsPossessed ) return;
		if ( !_body.IsValid() ) return;
		if ( IsProxy ) return;

		var player = PossessingPlayer;
		if ( !player.IsValid() ) return;

		var conn = player.Network?.Owner;
		if ( conn is null ) return;

		// Build movement direction from individual button presses via Connection
		var moveDir = Vector3.Zero;
		if ( conn.Down( "forward" ) ) moveDir += Vector3.Forward;
		if ( conn.Down( "backward" ) ) moveDir += Vector3.Backward;
		if ( conn.Down( "left" ) ) moveDir += Vector3.Left;
		if ( conn.Down( "right" ) ) moveDir += Vector3.Right;

		// Use the player's eye angles to determine movement direction relative to where they look
		var eyeAngles = player.Controller?.EyeAngles ?? Angles.Zero;
		var eyeRot = eyeAngles.ToRotation();

		if ( moveDir.LengthSquared > 0.01f )
		{
			var wishDir = eyeRot * moveDir.Normal;

			// Apply force in the horizontal plane
			var flatDir = wishDir.WithZ( 0 );
			if ( flatDir.LengthSquared > 0.01f )
			{
				// Force proportional to mass so different prop sizes feel consistent
				var mass = _body.Mass.Clamp( 1, 1000 );
				var moveForce = MaxSpeed * mass * 10f;
				_body.ApplyImpulse( flatDir * moveForce * Time.Delta );
			}
		}

		// Dampen angular velocity to prevent the prop from spinning out of control
		var angVel = _body.AngularVelocity;
		if ( angVel.Length > 1f )
		{
			_body.AngularVelocity = angVel * 0.95f;
		}

		// Clamp speed
		var velocity = _body.Velocity;
		var flatVel = velocity.WithZ( 0 );
		if ( flatVel.Length > MaxSpeed )
		{
			_body.Velocity = flatVel.Normal * MaxSpeed + Vector3.Up * velocity.z;
		}

		// Jump — apply upward impulse if we're roughly on the ground
		if ( conn.Pressed( "jump" ) && _timeSinceLastJump > 0.5f )
		{
			// Ground check: trace downward from the prop's center
			var groundCheck = Scene.Trace.FromTo( WorldPosition, WorldPosition + Vector3.Down * 48f )
				.IgnoreGameObjectHierarchy( GameObject )
				.Run();

			if ( groundCheck.Hit )
			{
				// JumpForce is the desired upward velocity — impulse = mass * velocity
				var mass = _body.Mass.Clamp( 1, 1000 );
				_body.ApplyImpulse( Vector3.Up * JumpForce * mass );
				// Kill angular velocity on jump so the prop doesn't spin wildly
				_body.AngularVelocity = 0;
				_timeSinceLastJump = 0;
			}
		}
	}

	void IDamageable.OnDamage( in DamageInfo damage )
	{
		if ( IsProxy ) return;

		// If the possessed prop takes enough damage, kick the player out
		var prop = GetComponent<Prop>();
		if ( !prop.IsValid() ) return;

		// If the prop would die, exit possession first
		if ( prop.Health <= 0 )
		{
			// Restore the player and exit
			var player = PossessingPlayer;
			if ( player.IsValid() )
			{
				player.Controller.ThirdPerson = false;
				if ( player.Body.IsValid() )
					player.Body.Enabled = true;

				var camController = player.GameObject.GetComponent<PossessionCameraController>();
				if ( camController.IsValid() )
					camController.Destroy();

				player.GameObject.Network.Refresh();
			}

			Depossess();
		}
	}
}
