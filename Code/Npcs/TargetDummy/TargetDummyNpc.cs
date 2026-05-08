using Sandbox.Npcs.Layers;
using Sandbox.Npcs.Schedules;
using Sandbox.Npcs.Tasks;

namespace Sandbox.Npcs.TargetDummy;

/// <summary>
/// A simple target dummy NPC that stands still and takes damage.
/// Perfect for testing hitbox display with the hitbox_npcs command.
/// </summary>
[Group( "NPC" )]
[Title( "Target Dummy" )]
public class TargetDummyNpc : Npc, Component.IDamageable
{
	[Property, ClientEditable, Range( 1, 500 ), Sync]
	public float Health { get; set; } = 200f;

	[Property]
	public float RespawnTime { get; set; } = 5f;

	private Vector3 _spawnPosition;
	private Rotation _spawnRotation;

	protected override void OnStart()
	{
		base.OnStart();
		_spawnPosition = WorldPosition;
		_spawnRotation = WorldRotation;
	}

	public override ScheduleBase GetSchedule()
	{
		return GetSchedule<TargetDummyIdleSchedule>();
	}

	void IDamageable.OnDamage( in DamageInfo damage )
	{
		if ( IsProxy ) return;

		Health -= damage.Damage;

		if ( Health < 1f )
		{
			Die( damage );
		}
	}

	protected override void Die( in DamageInfo damage )
	{
		CreateRagdoll( GetDeathLaunchVelocity( damage ), damage.Origin );
		_ = RespawnAsync();
	}

	private async Task RespawnAsync()
	{
		GameObject.Enabled = false;
		Health = 200f;

		await GameTask.DelaySeconds( RespawnTime );

		if ( !GameObject.IsValid() ) return;

		WorldPosition = _spawnPosition;
		WorldRotation = _spawnRotation;
		GameObject.Enabled = true;
	}

	/// <summary>
	/// Spawns a target dummy at the caller's eye position.
	/// </summary>
	[ConCmd( "spawn_target_dummy", Help = "Spawn a target dummy NPC in front of you" )]
	public static void SpawnTargetDummy( Connection source )
	{
		var player = Player.FindForConnection( source );
		if ( player == null ) return;

		var eyes = player.EyeTransform;
		var trace = Game.SceneTrace.Ray( eyes.Position, eyes.Position + eyes.Forward * 200 )
			.IgnoreGameObject( player.GameObject )
			.WithoutTags( "player" )
			.Run();

		var up = trace.Normal;
		var backward = -eyes.Forward;
		var right = Vector3.Cross( up, backward ).Normal;
		var forward = Vector3.Cross( right, up ).Normal;
		var facingAngle = Rotation.LookAt( forward, up );
		var spawnTransform = new Transform( trace.EndPosition, facingAngle );

		var go = GameObject.Clone( "entities/sents/npc/target_dummy.prefab", new CloneConfig { Transform = spawnTransform, StartEnabled = false } );
		go.Tags.Add( "removable" );
		go.NetworkSpawn( true, null );

		// Add to undo stack so player can undo the spawn
		var undo = player.Undo.Create();
		undo.Name = "Spawn Target Dummy";
		undo.Add( go );
	}
}

/// <summary>
/// Idle schedule for the target dummy — stands still and waits, looking at nearby targets.
/// </summary>
public class TargetDummyIdleSchedule : ScheduleBase
{
	protected override void OnStart()
	{
		// Look at nearest visible target if any
		if ( Npc.Senses.Nearest.IsValid() )
		{
			Npc.Animation.SetLookTarget( Npc.Senses.Nearest );
			AddTask( new LookAt( Npc.Senses.Nearest.WorldPosition ) );
		}

		// Wait then re-evaluate
		AddTask( new Wait( Game.Random.Float( 2f, 5f ) ) );
	}

	protected override void OnUpdate()
	{
		if ( Npc.Senses.Nearest.IsValid() )
		{
			Npc.Animation.SetLookTarget( Npc.Senses.Nearest );
		}
		else
		{
			Npc.Animation.ClearLookTarget();
		}
	}
}
