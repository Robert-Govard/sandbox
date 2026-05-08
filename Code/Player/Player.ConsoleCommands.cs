public sealed partial class Player
{
	/// <summary>
	/// Find a player for this connection
	/// </summary>
	public static Player FindForConnection( Connection c )
	{
		return Game.ActiveScene.GetAll<Player>().FirstOrDefault( x => x.Network.Owner == c );
	}

	/// <summary>
	/// Get player from a connecction id
	/// </summary>
	/// <param name="playerId"></param>
	/// <returns></returns>
	public static Player For( Guid playerId )
	{
		return Game.ActiveScene.GetAll<Player>().FirstOrDefault( x => x.PlayerId.Equals( playerId ) );
	}

	/// <summary>
	/// Kill yourself
	/// </summary>
	[ConCmd( "kill" )]
	public static void KillSelf( Connection source )
	{
		var player = Player.FindForConnection( source );
		if ( player is null ) return;

		player.KillSelf();
	}

	[Rpc.Host]
	internal void KillSelf()
	{
		if ( Rpc.Caller != Network.Owner ) return;

		this.OnDamage( new DamageInfo( float.MaxValue, GameObject, null ) );
	}

	[ConCmd( "god", ConVarFlags.Server | ConVarFlags.Cheat, Help = "Toggle invulnerability" )]
	public static void God( Connection source )
	{
		var player = PlayerData.For( source );
		if ( !player.IsValid() )
			return;

		player.IsGodMode = !player.IsGodMode;
		source.SendLog( LogLevel.Info, player.IsGodMode ? "Godmode enabled" : "Godmode disabled" );
	}

	[ConCmd( "noclip", ConVarFlags.Server | ConVarFlags.Cheat, Help = "Toggle noclip (fly through walls)" )]
	public static void Noclip( Connection source )
	{
		var player = Player.FindForConnection( source );
		if ( !player.IsValid() ) return;

		var noclip = player.GetComponent<NoclipMoveMode>( true );
		if ( !noclip.IsValid() ) return;

		noclip.Enabled = !noclip.Enabled;
		source.SendLog( LogLevel.Info, noclip.Enabled ? "Noclip enabled" : "Noclip disabled" );
	}

	/// <summary>
	/// Toggle free camera (client only; uses local scene camera).
	/// </summary>
	[ConCmd( "freecam", ConVarFlags.Cheat, Help = "Toggle free camera" )]
	public static void Freecam()
	{
		var scene = Game.ActiveScene;
		if ( scene is null ) return;

		var freecam = scene.Get<FreeCamGameObjectSystem>();
		if ( freecam is null ) return;

		freecam.Toggle();
		Log.Info( freecam.IsActive ? "Freecam enabled" : "Freecam disabled" );
	}

	/// <summary>.
	/// Set vertical field of view (saved as sb.fov).
	/// </summary>
	[ConCmd( "fov", Help = "Set vertical FOV in degrees (54-120); same as sb.fov" )]
	public static void SetFov( float degrees )
	{
		var v = Player.ApplyFovFromCommand( degrees );
		Log.Info( $"FOV set to {v}" );
	}

	/// <summary>
	/// Switch to another map
	/// </summary>
	[ConCmd( "map", ConVarFlags.Admin )]
	public static void ChangeMap( string mapName )
	{
		LaunchArguments.Map = mapName;
		Game.Load( Game.Ident, true );
	}

	/// <summary>
	/// Switch to another map
	/// </summary>
	[ConCmd( "undo", ConVarFlags.Server )]
	public static void RunUndo( Connection source )
	{
		var player = Player.FindForConnection( source );
		if ( !player.IsValid() )
			return;

		player.Undo.Undo();
	}
}
