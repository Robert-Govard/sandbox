/// <summary>
/// The local user's preferences in Deathmatch
/// </summary>
public static class GamePreferences
{
	/// <summary>
	/// Enables automatic switching to better weapons on item pickup
	/// </summary>
	[ConVar( "sb.autoswitch", ConVarFlags.UserInfo | ConVarFlags.Saved )]
	public static bool AutoSwitch { get; set; } = true;

	/// <summary>
	/// Enables fast switching between inventory weapons
	/// </summary>
	[ConVar( "sb.fastswitch", ConVarFlags.Saved )]
	public static bool FastSwitch { get; set; } = false;

	/// <summary>
	/// Intensity of your camera's screenshake
	/// </summary>
	[ConVar( "sb.viewbob", ConVarFlags.Saved )]
	[Group( "Camera" )]
	public static bool ViewBobbing { get; set; } = true;

	/// <summary>
	/// Vertical field of view for the first-person camera (degrees).
	/// </summary>
	[ConVar( "sb.fov", ConVarFlags.Saved )]
	[Range( 54f, 120f ), Step( 1f ), Group( "Camera" )]
	public static float FieldOfView { get; set; } = 90f;

	/// <summary>
	/// Intensity of your camera's screenshake
	/// </summary>
	[ConVar( "sb.screenshake", ConVarFlags.Saved )]
	[Range( 0.1f, 2f ), Step( 0.1f ), Group( "Camera" )]
	public static float Screenshake { get; set; } = 0.3f;

	/// <summary>
	/// Free camera movement speed multiplier
	/// </summary>
	[ConVar( "sb.freecam.speed", ConVarFlags.Saved )]
	[Range( 10f, 500f ), Step( 10f ), Group( "FreeCam" )]
	public static float FreeCamSpeed { get; set; } = 50f;

	/// <summary>
	/// Free camera smoothing amount (0 = no smoothing, 1 = maximum smoothing)
	/// </summary>
	[ConVar( "sb.freecam.smoothing", ConVarFlags.Saved )]
	[Range( 0f, 1f ), Step( 0.1f ), Group( "FreeCam" )]
	public static float FreeCamSmoothing { get; set; } = 0.5f;

	/// <summary>
	/// Free camera field of view
	/// </summary>
	[ConVar( "sb.freecam.fov", ConVarFlags.Saved )]
	[Range( 1f, 120f ), Step( 1f ), Group( "FreeCam" )]
	public static float FreeCamFov { get; set; } = 80f;
}
