using Sandbox.Npcs;
using Sandbox.Npcs.CombatNpc;
using Sandbox.Npcs.Scientist;
using Sandbox.Npcs.Rollermine;
using Sandbox.Npcs.TargetDummy;

/// <summary>
/// Debug component to show hitboxes for players and NPCs.
/// Automatically created when you use the hitbox console commands.
/// </summary>
[Group( "Debug" )]
public sealed class HitboxDebugComponent : Component
{
	[Property] public bool ShowPlayerHitboxes { get; set; } = false;
	[Property] public bool ShowNpcHitboxes { get; set; } = false;
	[Property] public bool ShowPlayerBones { get; set; } = false;
	[Property] public bool ShowNpcBones { get; set; } = false;
	[Property] public Color PlayerHitboxColor { get; set; } = Color.Green;
	[Property] public Color NpcHitboxColor { get; set; } = Color.Red;
	[Property, Range( 0.1f, 10f )] public float HitboxThickness { get; set; } = 1f;

	private static HitboxDebugComponent _instance;
	private Dictionary<GameObject, BBox> _cachedBounds = new();
	private HashSet<string> _hiddenBones = new();

	/// <summary>
	/// Get existing instance or create one automatically
	/// </summary>
	private static HitboxDebugComponent GetOrCreate()
	{
		if ( _instance != null && _instance.IsValid() ) return _instance;

		var go = new GameObject( true, "hitbox_debug" );
		go.Flags = GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		_instance = go.AddComponent<HitboxDebugComponent>();
		go.Enabled = true;
		return _instance;
	}

	protected override void OnEnabled()
	{
		_instance = this;
	}

	protected override void OnDisabled()
	{
		if ( _instance == this ) _instance = null;
	}

	protected override void OnUpdate()
	{
		var anyHitbox = ShowPlayerHitboxes || ShowNpcHitboxes;
		var anyBones = ShowPlayerBones || ShowNpcBones;

		if ( !anyHitbox && !anyBones )
			return;

		// Clean up cached bounds for destroyed objects
		foreach ( var key in _cachedBounds.Keys.ToList() )
		{
			if ( !key.IsValid() ) _cachedBounds.Remove( key );
		}

		foreach ( var player in Scene.GetAllComponents<Player>() )
		{
			if ( !player.IsValid() ) continue;

			if ( ShowPlayerHitboxes )
				DrawHitbox( player.GameObject, PlayerHitboxColor );

			if ( ShowPlayerBones )
			{
				var healthFrac = player.MaxHealth > 0 ? player.Health / player.MaxHealth : 1f;
				DrawBones( player.GameObject, healthFrac );
			}
		}

		foreach ( var npc in Scene.GetAllComponents<Npc>() )
		{
			if ( !npc.IsValid() ) continue;

			if ( ShowNpcHitboxes )
				DrawHitbox( npc.GameObject, NpcHitboxColor );

			if ( ShowNpcBones )
			{
				var healthFrac = GetNpcHealthFraction( npc );
				DrawBones( npc.GameObject, healthFrac );
			}
		}
	}

	private void DrawHitbox( GameObject go, Color color )
	{
		if ( !_cachedBounds.TryGetValue( go, out var bounds ) || bounds.Size.IsNearlyZero() )
		{
			bounds = go.GetLocalBounds();
			_cachedBounds[go] = bounds;
		}

		Gizmo.Transform = go.WorldTransform;

		// Solid fill
		Gizmo.Draw.Color = color.WithAlpha( 0.3f );
		Gizmo.Draw.LineBBox( bounds );

		// Wireframe edges
		Gizmo.Draw.IgnoreDepth = true;
		Gizmo.Draw.Color = color;
		Gizmo.Draw.LineThickness = HitboxThickness;
		Gizmo.Draw.LineBBox( bounds );

		// Reset
		Gizmo.Draw.LineThickness = 1;
		Gizmo.Draw.IgnoreDepth = false;
	}

	/// <summary>
	/// Get health fraction for an NPC by checking its concrete type.
	/// </summary>
	private float GetNpcHealthFraction( Npc npc )
	{
		float health = 100f;
		float maxHealth = 100f;

		if ( npc is CombatNpc combat )
		{
			health = combat.Health;
			maxHealth = 100f;
		}
		else if ( npc is ScientistNpc scientist )
		{
			health = scientist.Health;
			maxHealth = 100f;
		}
		else if ( npc is RollermineNpc roller )
		{
			health = roller.Health;
			maxHealth = 35f;
		}
		else if ( npc is TargetDummyNpc dummy )
		{
			health = dummy.Health;
			maxHealth = 200f;
		}

		return maxHealth > 0 ? (health / maxHealth).Clamp( 0f, 1f ) : 1f;
	}

	private void DrawBones( GameObject go, float healthFrac )
	{
		var renderer = go.GetComponentInChildren<SkinnedModelRenderer>();
		if ( renderer == null || !renderer.IsValid() ) return;

		var bones = renderer.GetBoneTransforms( true );
		var jointColor = Color.Lerp( Color.Red, Color.Yellow, healthFrac );
		var lineColor = jointColor.WithAlpha( 0.5f );
		var jointSize = 2f;

		Gizmo.Transform = new Transform();

		for ( int i = 0; i < bones.Length; i++ )
		{
			var bone = renderer.GetBoneObject( i );
			if ( bone == null || !bone.IsValid() ) continue;

			var boneName = bone.Name;

			// Skip hidden bones
			if ( _hiddenBones.Contains( boneName ) ) continue;

			var bonePos = bone.WorldPosition;

			// Draw joint sphere
			Gizmo.Draw.Color = jointColor;
			Gizmo.Draw.IgnoreDepth = true;
			Gizmo.Draw.LineSphere( bonePos, jointSize );

			// Draw line to parent bone
			var parent = bone.Parent;
			if ( parent != null && parent.IsValid() && parent != go )
			{
				// Walk up to find a bone that's in our set
				var parentBone = parent.GetComponentInParent<SkinnedModelRenderer>()?.GameObject == go.GetComponentInChildren<SkinnedModelRenderer>()?.GameObject
					? parent : null;

				if ( parentBone != null && !_hiddenBones.Contains( parentBone.Name ) )
				{
					Gizmo.Draw.Color = lineColor;
					Gizmo.Draw.LineThickness = HitboxThickness * 0.5f;
					Gizmo.Draw.Line( parentBone.WorldPosition, bonePos );
					Gizmo.Draw.LineThickness = 1;
				}
			}
		}

		Gizmo.Draw.IgnoreDepth = false;
	}

	[ConCmd( "hitbox", ConVarFlags.Cheat, Help = "Toggle all hitbox display" )]
	public static void ToggleAll()
	{
		var inst = GetOrCreate();
		var on = !inst.ShowPlayerHitboxes || !inst.ShowNpcHitboxes;
		inst.ShowPlayerHitboxes = on;
		inst.ShowNpcHitboxes = on;
		Log.Info( $"All hitboxes: {(on ? "ON" : "OFF")}" );
	}

	[ConCmd( "hitbox_players", ConVarFlags.Cheat, Help = "Toggle player hitbox display" )]
	public static void TogglePlayers()
	{
		var inst = GetOrCreate();
		inst.ShowPlayerHitboxes = !inst.ShowPlayerHitboxes;
		Log.Info( $"Player hitboxes: {(inst.ShowPlayerHitboxes ? "ON" : "OFF")}" );
	}

	[ConCmd( "hitbox_npcs", ConVarFlags.Cheat, Help = "Toggle NPC hitbox display" )]
	public static void ToggleNpcs()
	{
		var inst = GetOrCreate();
		inst.ShowNpcHitboxes = !inst.ShowNpcHitboxes;
		Log.Info( $"NPC hitboxes: {(inst.ShowNpcHitboxes ? "ON" : "OFF")}" );
	}

	[ConCmd( "hitbox_bones", ConVarFlags.Cheat, Help = "Toggle all bone display" )]
	public static void ToggleBones()
	{
		var inst = GetOrCreate();
		var on = !inst.ShowPlayerBones || !inst.ShowNpcBones;
		inst.ShowPlayerBones = on;
		inst.ShowNpcBones = on;
		Log.Info( $"All bones: {(on ? "ON" : "OFF")}" );
	}

	[ConCmd( "hitbox_bones_players", ConVarFlags.Cheat, Help = "Toggle player bone display" )]
	public static void TogglePlayerBones()
	{
		var inst = GetOrCreate();
		inst.ShowPlayerBones = !inst.ShowPlayerBones;
		Log.Info( $"Player bones: {(inst.ShowPlayerBones ? "ON" : "OFF")}" );
	}

	[ConCmd( "hitbox_bones_npcs", ConVarFlags.Cheat, Help = "Toggle NPC bone display" )]
	public static void ToggleNpcBones()
	{
		var inst = GetOrCreate();
		inst.ShowNpcBones = !inst.ShowNpcBones;
		Log.Info( $"NPC bones: {(inst.ShowNpcBones ? "ON" : "OFF")}" );
	}

	[ConCmd( "hitbox_bone_hide", ConVarFlags.Cheat, Help = "Hide a bone by name (e.g. hitbox_bone_hide head)" )]
	public static void HideBone( string boneName )
	{
		var inst = GetOrCreate();
		inst._hiddenBones.Add( boneName );
		Log.Info( $"Bone '{boneName}' hidden. Hidden: [{string.Join( ", ", inst._hiddenBones )}]" );
	}

	[ConCmd( "hitbox_bone_show", ConVarFlags.Cheat, Help = "Show a previously hidden bone by name" )]
	public static void ShowBone( string boneName )
	{
		var inst = GetOrCreate();
		inst._hiddenBones.Remove( boneName );
		Log.Info( $"Bone '{boneName}' shown. Hidden: [{string.Join( ", ", inst._hiddenBones )}]" );
	}

	[ConCmd( "hitbox_bone_showall", ConVarFlags.Cheat, Help = "Show all hidden bones" )]
	public static void ShowAllBones()
	{
		var inst = GetOrCreate();
		inst._hiddenBones.Clear();
		Log.Info( "All bones visible" );
	}

	[ConCmd( "hitbox_bone_list", ConVarFlags.Cheat, Help = "List all bones of the nearest player or NPC" )]
	public static void ListBones()
	{
		var inst = GetOrCreate();
		var player = Game.ActiveScene?.GetAllComponents<Player>().FirstOrDefault();
		var npc = Game.ActiveScene?.GetAllComponents<Npc>().FirstOrDefault();

		var target = player?.GameObject ?? npc?.GameObject;
		if ( target == null )
		{
			Log.Info( "No player or NPC found in scene" );
			return;
		}

		var renderer = target.GetComponentInChildren<SkinnedModelRenderer>();
		if ( renderer == null )
		{
			Log.Info( "No SkinnedModelRenderer found" );
			return;
		}

		var bones = renderer.GetBoneTransforms( true );
		Log.Info( $"Bones for {target.Name} ({bones.Length}):" );
		for ( int i = 0; i < bones.Length; i++ )
		{
			var bone = renderer.GetBoneObject( i );
			var name = bone?.Name ?? $"bone_{i}";
			var hidden = inst._hiddenBones.Contains( name );
			Log.Info( $"  {i}: {name}{(hidden ? " [HIDDEN]" : "")}" );
		}
	}

	[ConCmd( "hitbox_help", ConVarFlags.Cheat, Help = "Show hitbox debug commands" )]
	public static void ShowHelp()
	{
		Log.Info( "Hitbox Commands (cheat):" );
		Log.Info( "  hitbox              - toggle all hitboxes" );
		Log.Info( "  hitbox_players      - toggle player hitboxes" );
		Log.Info( "  hitbox_npcs         - toggle NPC hitboxes" );
		Log.Info( "  hitbox_bones        - toggle all bones" );
		Log.Info( "  hitbox_bones_players - toggle player bones" );
		Log.Info( "  hitbox_bones_npcs    - toggle NPC bones" );
		Log.Info( "  hitbox_bone_hide <name>  - hide a bone" );
		Log.Info( "  hitbox_bone_show <name>  - show a bone" );
		Log.Info( "  hitbox_bone_showall      - show all bones" );
		Log.Info( "  hitbox_bone_list         - list all bones" );
		Log.Info( "  hitbox_thickness <0.1-10> - line thickness" );
		Log.Info( "  hitbox_reset              - reset cached bounds" );
		Log.Info( "  hitbox_help               - this help" );
	}

	[ConCmd( "hitbox_thickness", ConVarFlags.Cheat, Help = "Set hitbox line thickness (0.1-10)" )]
	public static void SetThickness( float thickness )
	{
		var inst = GetOrCreate();
		inst.HitboxThickness = thickness.Clamp( 0.1f, 10f );
		Log.Info( $"Hitbox thickness: {inst.HitboxThickness}" );
	}

	[ConCmd( "hitbox_reset", ConVarFlags.Cheat, Help = "Reset cached hitbox bounds" )]
	public static void ResetBounds()
	{
		var inst = GetOrCreate();
		inst._cachedBounds.Clear();
		Log.Info( "Hitbox bounds cache reset" );
	}
}
