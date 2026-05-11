/// <summary>
/// A money printer entity that generates money over time for its owner.
/// Interact (press E) to open the printer menu with status and upgrades.
/// </summary>
[Alias( "moneyprinter" )]
public class MoneyPrinterEntity : Component, Component.IPressable
{
	/// <summary>
	/// The printer currently being viewed in the menu.
	/// </summary>
	public static MoneyPrinterEntity MenuTarget { get; set; }

	/// <summary>
	/// Base money generated per second before upgrades.
	/// </summary>
	[Property, ClientEditable, Range( 1, 100 )]
	public float BaseMoneyPerSecond { get; set; } = 5f;

	/// <summary>
	/// Base print interval in seconds before upgrades.
	/// </summary>
	[Property, ClientEditable, Range( 0.1f, 10f )]
	public float BasePrintInterval { get; set; } = 1.0f;

	[Sync] public int MoneyUpgradeLevel { get; set; }
	[Sync] public int SpeedUpgradeLevel { get; set; }
	[Property] public int MaxUpgradeLevel { get; set; } = 10;
	[Sync] public bool IsActive { get; private set; } = true;
	[Sync] public long TotalPrinted { get; private set; }
	[Sync] public long OwnerSteamId { get; set; }

	public float EffectiveMoneyPerSecond => BaseMoneyPerSecond * (1f + MoneyUpgradeLevel * 0.5f);
	public float EffectivePrintInterval => BasePrintInterval / (1f + SpeedUpgradeLevel * 0.3f);
	public long MoneyUpgradeCost => GetUpgradeCost( MoneyUpgradeLevel );
	public long SpeedUpgradeCost => GetUpgradeCost( SpeedUpgradeLevel );

	private RealTimeSince _timeSincePrint;

	protected override void OnUpdate()
	{
		if ( IsProxy ) return;
		if ( !IsActive ) return;

		if ( _timeSincePrint >= EffectivePrintInterval )
		{
			_timeSincePrint = 0;
			PrintMoney();
		}
	}

	private void PrintMoney()
	{
		var moneyToGive = (long)MathF.Ceiling( EffectiveMoneyPerSecond * EffectivePrintInterval );
		if ( moneyToGive <= 0 ) return;

		TotalPrinted += moneyToGive;

		var ownerData = PlayerData.All.FirstOrDefault( p => p.SteamId == OwnerSteamId );
		if ( ownerData.IsValid() )
		{
			ownerData.AddMoney( moneyToGive );
		}
	}

	public long GetUpgradeCost( int currentLevel ) => 50 * (long)MathF.Pow( 2, currentLevel );

	[Rpc.Host]
	public void ToggleActive()
	{
		IsActive = !IsActive;
		if ( IsActive )
			_timeSincePrint = 0;
	}

	[Rpc.Host]
	public void UpgradeMoney()
	{
		if ( MoneyUpgradeLevel >= MaxUpgradeLevel ) return;
		var ownerData = PlayerData.All.FirstOrDefault( p => p.SteamId == OwnerSteamId );
		if ( !ownerData.IsValid() ) return;
		if ( !ownerData.SpendMoney( MoneyUpgradeCost ) ) return;
		MoneyUpgradeLevel++;
	}

	[Rpc.Host]
	public void UpgradeSpeed()
	{
		if ( SpeedUpgradeLevel >= MaxUpgradeLevel ) return;
		var ownerData = PlayerData.All.FirstOrDefault( p => p.SteamId == OwnerSteamId );
		if ( !ownerData.IsValid() ) return;
		if ( !ownerData.SpendMoney( SpeedUpgradeCost ) ) return;
		SpeedUpgradeLevel++;
	}

	// IPressable - for tooltip only, menu opening handled by MoneyPrinterMenu UI

	IPressable.Tooltip? IPressable.GetTooltip( IPressable.Event e )
	{
		var status = IsActive ? "Active" : "Inactive";
		return new IPressable.Tooltip( "Money Printer", "payments", $"{status} - ${EffectiveMoneyPerSecond:F0}/s" );
	}

	bool IPressable.CanPress( IPressable.Event e ) => true;

	bool IPressable.Press( IPressable.Event e )
	{
		MenuTarget = this;
		return true;
	}

	bool IPressable.Pressing( IPressable.Event e ) => false;
	void IPressable.Release( IPressable.Event e ) { }
}
