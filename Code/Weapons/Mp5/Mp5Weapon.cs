using Sandbox.Rendering;

public class Mp5Weapon : IronSightsWeapon
{
	[Property] public float TimeBetweenShots { get; set; } = 0.1f;

	protected override float GetPrimaryFireRate() => TimeBetweenShots;

	public override void PrimaryAttack()
	{
		ShootBullet( TimeBetweenShots, GetBullet() );
	}

	public override void DrawCrosshair( HudPainter hud, Vector2 center )
	{
		var gap = 1 + GetAimConeAmount() * 32;
		var len = 5;
		var w = 2f;

		var color = !HasAmmo() || IsReloading() || TimeUntilNextShotAllowed > 0 ? CrosshairNoShoot : CrosshairCanShoot;

		hud.SetBlendMode( BlendMode.Lighten );
		hud.DrawLine( center + Vector2.Left * (len + gap), center + Vector2.Left * gap, w, color );
		hud.DrawLine( center - Vector2.Left * (len + gap), center - Vector2.Left * gap, w, color );
		hud.DrawLine( center + Vector2.Up * (len + gap), center + Vector2.Up * gap, w, color );
		hud.DrawLine( center - Vector2.Up * (len + gap), center - Vector2.Up * gap, w, color );
	}
}
