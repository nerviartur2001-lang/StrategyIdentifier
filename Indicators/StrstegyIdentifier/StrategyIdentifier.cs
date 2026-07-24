#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations; 
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;                
using System.Windows.Input;
using System.Windows.Media;                
using System.Windows.Threading;           
using System.Xml.Serialization;     
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.NinjaScript;
using NinjaTrader.Gui.NinjaScript.AtmStrategy;
using NinjaTrader.NinjaScript;
#endregion

using NinjaTrader.NinjaScript.AddOns.AddOnsStrategyIdentifier.ChartTraderService;
using NinjaTrader.NinjaScript.AddOns.AddOnsStrategyIdentifier.StrategySnapshotManager;
using NinjaTrader.NinjaScript.AddOns.AddOnsStrategyIdentifier.RenderHelpers;
using NinjaTrader.NinjaScript.AddOns.AddOnsStrategyIdentifier.NearestStrategyData;
using NinjaTrader.NinjaScript.AddOns.AddOnsStrategyIdentifier.NtNullLogger;
using NinjaTrader.NinjaScript.AddOns.AddOnsStrategyIdentifier.UiThreadHelper;

public enum StrategyCalculationMode { Personal = 0, Aggregated = 1 }

//This namespace holds Indicators in this folder and is required. Do not change it. 
namespace NinjaTrader.NinjaScript.Indicators.StrategyIdentifier
{
	public class StrategyIdentifier : Indicator
	{
		public override string DisplayName { get => string.IsNullOrEmpty(displayNameOverride) ? "StrategyIdentifier" : displayNameOverride; }	
        // ==========================================================	
		#region Variables			
        // ──────────────────────────────		
		#region Common variables	
		
		private IStrategyLogger logger = NullLogger.Instance;
		private UiThreadHelper uiThreadHelper;
		private StrategyCalculationMode strategyMode;
				
		private Account currentAccount;
		private Instrument currentInstrument;	

		private double marketPrice = double.NaN;
		private double closePrice = double.NaN;		
		private double bidPrice = double.NaN;
		private double askPrice = double.NaN;	

		private double pointValue;
		private double tickSize;

		private string displayNameOverride = string.Empty;

		private bool isStatusConnected = false;

		#endregion
        // ──────────────────────────────			
		#region StrategySnapshotManager variables
		
		private StrategyRegistry registry;
		private MergedStrategySnapshot currentMergedSnapshot = MergedStrategySnapshot.Empty;

		private readonly Dictionary<(SlotKey, StrategyCalculationMode), 
			MergedStrategySnapshot> snapshotCache = new Dictionary<(SlotKey, StrategyCalculationMode), MergedStrategySnapshot>();
			
		private long lastLoggedSnapshotVersion = -1;
		
		private string nsEntrySignalNames = "Entry";
		private string nsStopSignalNames = "Stop";
		private string nsTargetSignalNames = "Target";
		
		#endregion			
        // ──────────────────────────────	
		#region ChartTraderService variables

		private Chart chartWindow;				
		private ChartTrader chartTrader;
		private IChartTraderService chartTraderService;	
		private bool isChartTraderAvailable;	

		#endregion
        // ──────────────────────────────			
		#region NearestStrategyData variables
		
		private readonly NearestLevelResolver nearestLevelResolver = new NearestLevelResolver(GetOrderActionName, GetOrderTypeName);
		
		private ChartHoverTracker chartHoverTracker;		
        private bool isChartControlCursorHand => chartHoverTracker?.IsChartControlCursorHand ?? false;		
		private NearestLevelInfo nearestLevelInfo => chartHoverTracker?.NearestLevelInfo ?? NearestLevelInfo.Empty;
        private bool isMouseInsideChartPanel => chartHoverTracker?.IsMouseInsideChartPanel ?? false;
        private float hoverY => chartHoverTracker?.HoverY ?? 0f;
        private double hoverPrice => chartHoverTracker?.HoverPrice ?? double.NaN;		
		
		private bool isMouseHandlersAttached;				

		#endregion			
        // ──────────────────────────────	
		#region Rendering Variables

		private const double TextOpacity = 100;
		private const double BorderOpacity = 100;					
		private const double BackgroundOpacity = 100;
		
		private const float MarkerHeightPadding = 4f;
		private const float MinFontScale = 0.5f;
				
		private const float MinWidthFactor = 5.0f;		
		private const float MarkerGroupGap = 1.0f;
		private const float OrderLevelMarkerSegmentGap = 2.0f;
		private const float AverageMarkerSegmentGap = 1.0f;		
		private const float OffsetFactor = 5.0f;	
		private const float PaddingSize = 13.0f;
		private const float BaseFontSize = 11.0f;				

		private enum BrushKey {
			Text, 
			Border,
			BackMarker,
			BackAmountUp, 
			BackAmountDown, 
			BackEntry, 
			BackTarget, 
			BackStop,
			BackQuantityUp, 
			BackQuantityDown }	
		
        private ChartScale chartScale;

        private SharpDXBrushManager<BrushKey> brushManager;
		private TextLayoutDXManager textLayoutManager;
		private SegmentMarkerRender markerSegmentRender;

		private SharpDX.Direct2D1.RoundedRectangle reusableRoundedRect;
		
		private SharpDX.Vector2 v1 = new SharpDX.Vector2();
		private SharpDX.Vector2 v2 = new SharpDX.Vector2();			

        private SharpDX.DirectWrite.TextFormat dxTextFormat;

		private double activeFontScale = 1.0;
		private float lastAppliedFontSize = 0.0f;	
		private float fontSizeRatio = 1.0f;
			
		private int pendingRenderInvalidate = 0;			
		private int? previousZOrder = null;
		private long lastZOrderSnapshotVersion = -1L;

        private float panelRight = 0.0f;
		private float panelLeft = 0.0f;
		private float panelTop = 0.0f;
		private float panelBottom = 0.0f;			

		private float heightMarkerRect = 0.0f;
		private float offsetMarkerRect = 0.0f;		
		private float paddingMarkerRect = 0.0f;
		private float averageEndMarkerX = 0.0f;
		private readonly List<AverageMarkerGroupRender> pendingAverageMarkerGroups = new List<AverageMarkerGroupRender>();
		private float pendingAverageMarkHeight = 0.0f;
		private float pendingAverageSegmentGap = 0.0f;
		
		private int cachedOrderDisplayBar;
		private float cachedChartWidth;
		private float orderDisplayBarPixelLength;	

		private SharpDX.Direct2D1.Brush textDxBrush;
		private SharpDX.Direct2D1.Brush borderDxBrush;		
		private SharpDX.Direct2D1.Brush markerBackDxBrush;						
		private SharpDX.Direct2D1.Brush upAmountBackDxBrush;		
		private SharpDX.Direct2D1.Brush downAmountBackDxBrush;
        private SharpDX.Direct2D1.Brush entryBackDxBrush;
		private SharpDX.Direct2D1.Brush targetBackDxBrush;
		private SharpDX.Direct2D1.Brush stopBackDxBrush;
		private SharpDX.Direct2D1.Brush upQuantityBackDxBrush;		
		private SharpDX.Direct2D1.Brush downQuantityBackDxBrush;		

		#endregion	
        // ──────────────────────────────			
		#endregion		
        // ==========================================================
		#region OnStateChange		
				
		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description =
				    @"- Shows live strategy activity directly on the chart. Draws price levels and labels for entries, targets, and stops of your running strategies." +				
				    "\n- The cold start of the indicator (the very first indicator installed in the platform) is performed on a clean chart without strategies, open positions and orders." +			
				    "\n- Two display modes are available: Personal (each strategy shown separately) or Aggregated (combined across strategies sharing the same account/instrument)." +			
				    "\n- If you use NinjaScript strategies with custom stop/target order names, enter them under the 'NinjaScript Strategy'" +
				    "settings group (NS Stop Names / NS Target Names) so the indicator can recognize and label them correctly.";
				Name = "StrategyIdentifier";
				Calculate = Calculate.OnBarClose;
				IsOverlay = true;
				DisplayInDataBox = false;
				PaintPriceMarkers = true;
				IsSuspendedWhileInactive = false;
				ScaleJustification = NinjaTrader.Gui.Chart.ScaleJustification.Right;
				//Disable this property if your indicator requires custom values that cumulate with each new market data event. 
				//See Help Guide for additional information.
				IsSuspendedWhileInactive = true;
				
				StrategyMode = StrategyCalculationMode.Personal;
				
				BrushText = Brushes.White;
				BrushBorder = Brushes.PowderBlue;					
				BrushBackMarker = Brushes.DarkSlateGray;			
				BrushBackAmountUp = Brushes.DarkGreen;
				BrushBackAmountDown = Brushes.DarkRed;
				BrushBackEntry = Brushes.SteelBlue;
				BrushBackTarget = Brushes.ForestGreen;
				BrushBackStop = Brushes.Red;
				BrushBackQuantityUp = Brushes.DarkGreen;
				BrushBackQuantityDown = Brushes.DarkRed;				

				ShowStratAvgPriceMarker = true;
				ShowAvgPrice = false;				
				ShowUnPnl = true;									
				AddOrderMarkerOffset = 0.0f;
				AddAverageMarkerOffset = 0.0f;
				HorizontalLineWidth = 2.0f;			
				IsBoldText = false;	

				NsStopSignalNames = "Stop";
				NsTargetSignalNames = "Target";
				
				EnableDebugLogging = false;
				LogOutputTab = PrintTo.OutputTab2;
				IClearOutputWindow = false;				
			}
			else if (State == State.DataLoaded)
			{
				logger = new NtLogger 
				{ 
					OutputTab = LogOutputTab,
					MinLevel = EnableDebugLogging 
						? AddOns.AddOnsStrategyIdentifier.NtNullLogger.LogLevel.Debug 
						: AddOns.AddOnsStrategyIdentifier.NtNullLogger.LogLevel.Error 
				};
				uiThreadHelper = new UiThreadHelper(
					() => ChartControl?.Dispatcher ?? Application.Current?.Dispatcher, logger, "StrategyIdentifier");
				chartHoverTracker = new ChartHoverTracker(nearestLevelResolver);
			}
			else if (State == State.Configure)
			{	
			    if (IClearOutputWindow)
			    	ClearOutputWindow();
			}		
			else if (State == State.Historical)
			{
			    RunOnUiThreadAsync(async () =>
			    {
					InitiateStrategySnapshotManager();					
					await InitiateChartTraderServiceAsync();					
					GetChartDataIfNeeded();
			        DetachMouseHandlers();
			        AttachMouseHandlers();	
				    InitiateRenderResources();
			    });			
			}
			else if (State == State.Terminated)
			{
				CleanupStrategySnapshotManager();				
				CleanupChartTraderService();				
				DetachMouseHandlers();
				CleanupRenderResources();
				logger = null;
			}
		}

		#endregion	
        // ==========================================================		
		#region Market Events				

		protected override void OnMarketData(MarketDataEventArgs e)
		{
			if (CurrentBar < 0) return;
			
		    try
		    {					
	            switch (e.MarketDataType)
	            {
	                case MarketDataType.Bid:
	                    bidPrice = e.Price;
	                    break;		
	                case MarketDataType.Ask:
	                    askPrice = e.Price;
	                    break;		
	                case MarketDataType.Last:
	                    marketPrice = e.Price;
	                    if (!double.IsNaN(e.Bid) && e.Bid > 0) bidPrice = e.Bid;
	                    if (!double.IsNaN(e.Ask) && e.Ask > 0) askPrice = e.Ask;
	                    break;		
	                default:
	                    break;
				}
		    }
		    catch (Exception ex) { logger.Error($"StrategyIdentifier.OnMarketData error: {ex.Message}"); } 
		}	

		protected override void OnBarUpdate()
		{
			if (CurrentBar < 0) return;			
			
		    try { closePrice = Close[0]; }
		    catch { closePrice = double.NaN; }
		}
		
		protected override void OnConnectionStatusUpdate(ConnectionStatusEventArgs connectionStatusUpdate)
		{
			isStatusConnected = connectionStatusUpdate.Status == ConnectionStatus.Connected;
			logger.Debug($"Connection status: {connectionStatusUpdate.Status}");
		}		
				
		#endregion
        // ==========================================================
		#region StrategySnapshotManager
				
		private void InitiateStrategySnapshotManager()
		{
		    try
		    {
		        if (registry != null)
		        {
		            if (ValidateAccount(currentAccount) && ValidateInstrument(currentInstrument))
		                _ = registry.EnsureSlotExistsAsync(currentAccount, currentInstrument);
		            return;
		        }
		
		        registry = StrategyRegistry.Acquire((func, prio) => RunOnUiThreadAsync(func, prio), logger);
		        registry.MergedSnapshotChanged += OnMergedSnapshotChanged;
		        registry.UpdateSignalNamesForAll(NsStopSignalNames, NsTargetSignalNames);
		
		        _ = registry.ScanAllAccountsAsync();
		    }
		    catch (Exception ex) { logger.Error($"StrategyIdentifier.InitiateRegistry error: {ex.Message}"); }
		}

		private void OnMergedSnapshotChanged(StrategyCalculationMode mode, MergedStrategySnapshot merged)
		{
		    if (merged == null) return;

			SlotKey? cacheKey = null;
			
			if (merged.SlotReports?.Count > 0)
			    cacheKey = merged.SlotReports[0].Key;
			else if (ValidateAccount(currentAccount) && ValidateInstrument(currentInstrument))
			    cacheKey = new SlotKey(currentAccount, currentInstrument);
			
			if (cacheKey.HasValue)
			    snapshotCache[(cacheKey.Value, mode)] = merged;

		    if (mode != strategyMode) return;

		    if (!IsSnapshotForCurrentChartState(merged)) return;

		    if (merged.Version == lastLoggedSnapshotVersion) return;
		    lastLoggedSnapshotVersion = merged.Version;

		    if (merged.IsEmpty)
		    {
		        currentMergedSnapshot = MergedStrategySnapshot.Empty;
		        ClearNearestStrategyDataForced();
		        InvokeOnUiThread(() => RequestRender());
		        return;
		    }

		    currentMergedSnapshot = merged;
			if (!isChartControlCursorHand)
			    ClearNearestStrategyDataForced();
		
		    DebugOnMergedSnapshotChanged(merged);
		    DebugPositionStates();
		
		    InvokeOnUiThread(() => RequestRender());
		}
				
		private bool IsSnapshotForCurrentChartState(MergedStrategySnapshot merged)
		{
		    if (!ValidateAccount(currentAccount) || !ValidateInstrument(currentInstrument)) return false;
		    if (merged == null) return false;
		    if (merged.SlotReports == null || merged.SlotReports.Count == 0) return merged.IsEmpty;

		    var key = new SlotKey(currentAccount, currentInstrument);
		    return merged.SlotReports.Any(r => r.Key.Equals(key));
		}
		
		private async Task SwitchToCurrentSlotAsync(Account account, Instrument instrument, string operationName)
		{
		    try
		    {				
			    if (ValidateAccount(account) && ValidateInstrument(instrument))
			        await RestoreOrCreateSlotAsync(account, instrument, operationName);
						
			    displayNameOverride =
				    $"StrategyIdentifier ({account?.Name ?? string.Empty} | " +
				    $"{instrument?.FullName ?? string.Empty} | " +
				    $"{BarsPeriod})";
			
			    RequestRender(); 
		    }
		    catch (Exception ex) { logger.Error($"StrategyIdentifier.SwitchToCurrentSlotAsync error: {ex.Message}\n{ex.StackTrace}"); }				
		}		
				
		private async Task RestoreOrCreateSlotAsync(Account account, Instrument instrument, string operationName)
		{
		    await registry.EnsureSlotExistsAsync(account, instrument).ConfigureAwait(false);
			
		    var key = new SlotKey(account, instrument);

		    var liveSnapshot = registry.GetMergedSnapshot(account, instrument, strategyMode);

		    MergedStrategySnapshot cachedSnapshot = (liveSnapshot != null && !liveSnapshot.IsEmpty) ? liveSnapshot : null;

		    if (cachedSnapshot != null)
		        snapshotCache[(key, strategyMode)] = cachedSnapshot;
		    else
		        snapshotCache.TryGetValue((key, strategyMode), out cachedSnapshot);

		    if (cachedSnapshot != null && !cachedSnapshot.IsEmpty)
		    {
		        currentMergedSnapshot = cachedSnapshot;
		        logger.Debug($"StrategyIdentifier.RestoreOrCreateSlotAsync => {operationName}: RESTORED snapshot (live from registry) [{key}]");           
		    }
		    else
		    {
		        currentMergedSnapshot = MergedStrategySnapshot.Empty;
		        ClearNearestStrategyDataForced();
		    }
		}		
		
		private void CleanupStrategySnapshotManager()
		{
		    try
		    {
		        if (registry != null)
		        {
		            registry.MergedSnapshotChanged -= OnMergedSnapshotChanged;
		            StrategyRegistry.Release(registry);
		            registry = null;
		        }
		
		        currentMergedSnapshot = MergedStrategySnapshot.Empty;
		        snapshotCache.Clear(); 
		    }
		    catch (Exception ex) { logger.Error($"StrategyIdentifier.CleanupRegistry error: {ex.Message}"); }
		}

		private void DebugOnMergedSnapshotChanged(MergedStrategySnapshot merged) => 
			DebugSnapshotState.LogMergedSnapshot(logger, merged, currentAccount, currentInstrument, FormatPrice);

		private void DebugPositionStates()
		{
			var states = registry?.GetPositionStatesSnapshot(currentAccount, currentInstrument);
			DebugSnapshotState.LogPositionStates(logger, states, FormatPrice);
		}
		
		#endregion
        // ==========================================================
		#region ChartTraderService	

		private async Task InitiateChartTraderServiceAsync()
		{
		    try
		    {
	            chartWindow = Window.GetWindow(ChartControl.Parent) as Chart;		
	            if (chartWindow == null) return; 					
				
		        chartTrader = ChartControl.OwnerChart?.ChartTrader;

		        if (chartTrader == null)
		        {
		            CleanupChartTraderService();
		            return;
		        }

		        if (chartTraderService == null)
		        {
		            chartTraderService = new ChartTraderService(
		                (action, prio) => RunOnUiThreadAsync(() => { action(); return Task.CompletedTask; }, prio),
		                InvokeOnUiThread,
		                PostToUiThread,
						logger);
		
		            chartTraderService.StateChanged += OnChartTraderStateChanged;
		        }

		        await chartTraderService.InitializeAsync(chartWindow, chartTrader);								
		    }
		    catch (Exception ex) { logger.Error($"StrategyIdentifier.InitChartTraderIntegrationAsync error: {ex.Message}"); }
		}
		
		private void OnChartTraderStateChanged(ChartTraderState state)
		{
		    if (state == null) return;
			
		    isChartTraderAvailable = state.IsAvailable;

		    if (!isChartTraderAvailable)
		    {
		        currentAccount = null;
		        currentInstrument = null;
		        if (registry != null)
		            GetChartDataIfNeeded();
		        return;
		    }			
						
		    var newAccount = state.Account;
		    var newInstrument = state.Instrument;
			
			if (!ValidateAccount(newAccount) || !ValidateInstrument(newInstrument)) return; 
			
		    var previousAccount = currentAccount;
		    var previousInstrument = currentInstrument;

		    bool sameAccount = AreSameAccount(previousAccount, newAccount);
		    bool sameInstrument = AreSameInstrument(previousInstrument, newInstrument);		

		    if (newInstrument?.MasterInstrument != null)
		    {
		        pointValue = newInstrument.MasterInstrument.PointValue;
		        tickSize = newInstrument.MasterInstrument.TickSize;
		    }
		
		    if (sameAccount && sameInstrument) return;
		
		    currentAccount = newAccount;
		    currentInstrument = newInstrument;		
		
		    logger.Debug(
				$"StrategyIdentifier.OnChartTraderStateChanged: CHANGE DETECTED | " +
				$"Account={currentAccount?.Name ?? "null"} | " +
				$"Instrument={currentInstrument?.FullName ?? "null"} | " +						
				$"PointValue={pointValue} | " + 
				$"TickSize={tickSize}"
			);		

			_ = RunOnUiThreadAsync(async () =>
			{
			    try { await SwitchToCurrentSlotAsync(currentAccount, currentInstrument, "OnChartTraderStateChanged"); }
			    catch (Exception ex) { logger.Error($"StrategyIdentifier.OnChartTraderStateChanged error: {ex.Message}"); }
			});
		}
		
		private static bool AreSameAccount(Account a, Account b)
		{
		    if (ReferenceEquals(a, b)) return true;
		    if (a == null || b == null) return false;
		    return string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
		}
		
		private static bool AreSameInstrument(Instrument a, Instrument b)
		{
		    if (ReferenceEquals(a, b)) return true;
		    if (a == null || b == null) return false;
		    return string.Equals(a.FullName, b.FullName, StringComparison.OrdinalIgnoreCase);
		}		
		
		private void CleanupChartTraderService()
		{
			try
			{
				if (chartTraderService != null)
				{
					chartTraderService.StateChanged -= OnChartTraderStateChanged;
					try { chartTraderService?.Dispose(); } catch { /* ignore */ }
					chartTraderService = null;
				}
				
				isChartTraderAvailable = false;	
				chartTrader = null;
				chartWindow = null;
			}
			catch (Exception ex) { logger.Error($"StrategyIdentifier.CleanupChartTraderService error: {ex.Message}"); }
		}		
					 
		#endregion				
        // ==========================================================		
		#region GetChartDataIfNeeded 	

		private void GetChartDataIfNeeded()
		{
		    try
		    {
				if (isChartTraderAvailable) return;
		        if (ValidateAccount(currentAccount) && ValidateInstrument(currentInstrument)) return;
		
		        var instrument = ChartControl?.Instrument;
		        if (instrument == null) return;
		
		        pointValue = instrument.MasterInstrument?.PointValue ?? 0.0;
		        tickSize = instrument.MasterInstrument?.TickSize  ?? 0.0;
		
		        if (pointValue <= 0.0 || tickSize <= 0.0) return;
		
		        currentInstrument = instrument;

		        Account matched = null;
		        lock (Account.All)
		        {
		            foreach (var a in Account.All)
		            {
		                if (a == null || a.ConnectionStatus != ConnectionStatus.Connected) continue;
		
		                bool hasPosition = a.Positions?.Any(p =>
		                    p != null &&
		                    p.Instrument?.FullName == instrument.FullName &&
		                    p.MarketPosition != MarketPosition.Flat) ?? false;
		
		                bool hasOrders = a.Orders?.Any(o =>
		                    o != null &&
		                    o.Instrument?.FullName == instrument.FullName &&
		                    !Order.IsTerminalState(o.OrderState)) ?? false;
		
		                if (hasPosition || hasOrders)
		                {
		                    matched = a;
		                    break;
		                }
		            }
		
		            // Fallback
		            if (matched == null)
		                matched = Account.All.FirstOrDefault(a => 
							a != null && 
							a.ConnectionStatus == ConnectionStatus.Connected);                   
		        }
		
		        if (matched == null) return;
		
		        currentAccount = matched;
		
			    logger.Debug(
					$"StrategyIdentifier.GetChartDataIfNeeded: CHANGE DETECTED | " +
					$"Account={currentAccount?.Name ?? "null"} | " +
					$"Instrument={currentInstrument?.FullName ?? "null"} | " +						
					$"PointValue={pointValue} | " + 
					$"TickSize={tickSize}"
				);	
				
			    _ = SwitchToCurrentSlotAsync(currentAccount, currentInstrument, "GetChartDataIfNeeded");					
		    }
		    catch (Exception ex) { logger.Error($"StrategyIdentifier.GetChartDataIfNeeded error: {ex.Message}"); }
		}

		#endregion						
        // ==========================================================
		#region Helpers		
        // ──────────────────────────────		
		#region OnUiThread Helpers	

		private void InvokeOnUiThread(Action action, DispatcherPriority priority = DispatcherPriority.Normal)
			=> uiThreadHelper.InvokeOnUiThread(action, priority);

		private void PostToUiThread(Action action, DispatcherPriority priority = DispatcherPriority.Normal)
			=> uiThreadHelper.PostToUiThread(action, priority);

		private Task RunOnUiThreadAsync(Func<Task> func, DispatcherPriority priority = DispatcherPriority.Normal)
			=> uiThreadHelper.RunOnUiThreadAsync(func, priority);

		#endregion			
        // ──────────────────────────────			
		#region Price Helpers
		
		private static double ResolveCurrentPrice(double marketPrice, double closePrice)
		{
		    if (!double.IsNaN(marketPrice) && marketPrice > 0) return marketPrice;
		    if (!double.IsNaN(closePrice) && closePrice > 0) return closePrice;
		    return double.NaN;
		}		

		private double GetCurrentPriceForUnPnl(double market, double close, double bid, double ask, bool isLong, bool isShort)
		{
		    if (isLong)
		        if (!double.IsNaN(bid) && bid > 0) return bid;
		    else if (isShort)
		        if (!double.IsNaN(ask) && ask > 0) return ask;

		    return ResolveCurrentPrice(market, close);
		}	
		
		private string FormatPrice(double price)
		{
		    if (tickSize <= 0.0) return price.ToString("N2", Core.Globals.GeneralOptions.CurrentCulture);

		    int decimals = 0;
		    double t = tickSize;
		    while (t < 1.0 - 1e-10)
		    {
		        t *= 10.0;
		        decimals++;
		    }
		    decimals = Math.Max(2, Math.Min(decimals, 8));
		
		    string fmt = "N" + decimals;
		    return price.ToString(fmt, Core.Globals.GeneralOptions.CurrentCulture);
		}

		#endregion				
        // ──────────────────────────────
		#region Validation Helpers

		private bool ValidateAccount(Account account)
		{			
		    try { if (account == null) return false; lock (Account.All) { return Account.All.Contains(account); } }
		    catch (Exception ex) { logger.Error($"StrategyIdentifier : Error in ValidateAccount {ex.Message}"); return false; }
		}

		private bool ValidateInstrument(Instrument instrument)
		{
		    try { if (instrument == null) return false; return !string.IsNullOrWhiteSpace(instrument.FullName) && instrument.MasterInstrument != null; } 
		    catch (Exception ex) { logger.Error($"StrategyIdentifier : Error in ValidateInstrument {ex.Message}"); return false; }  
		}	

		private bool ValidateAtmStrategy(AtmStrategy strategy)
		{
		    try { if (strategy == null) return false; return true; }
		    catch (Exception ex) { logger.Error($"StrategyIdentifier : Error in ValidateAtmStrategy {ex.Message}"); return false; }
		}			
		
		#endregion
        // ──────────────────────────────		
		#endregion			
        // ==========================================================				
		#region Rendering
        // ──────────────────────────────		
		#region OnRender		
			
		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
		    if (!isStatusConnected) return;
			
			this.chartScale = chartScale;
		    base.OnRender(chartControl, chartScale);
		
		    if (RenderTarget == null || chartControl == null || chartScale == null) return;

			UpdateChartCursorState();		
			UpdateRenderMetrics(chartControl, chartScale, chartTrader);

		    try
		    {
				DrawStrategyAveragePriceMarker(chartScale, heightMarkerRect, offsetMarkerRect, paddingMarkerRect);			
				DrawStrategyOrderLevelMarkers(chartScale, heightMarkerRect, offsetMarkerRect, paddingMarkerRect);
				RenderPendingAveragePriceMarkers();
		    }
		    catch (Exception ex) { logger.Error($"StrategyIdentifier.OnRender error: {ex.Message}"); }
		}	

		#endregion			
        // ──────────────────────────────				
		#region UpdateRenderMetrics

		private void UpdateRenderMetrics(ChartControl chartControl, ChartScale chartScale, ChartTrader chartTrader)
		{
		    UpdatePanelBounds(chartControl, chartScale);
		    UpdateFontAndMarkerMetrics(chartControl);
			UpdateOrderDisplayBarLength(chartTrader);
		}		

		private void UpdatePanelBounds(ChartControl chartControl, ChartScale chartScale)
		{
			if (chartControl == null || chartScale == null) return;			
			
		    float newPanelRight = chartControl.CanvasRight;
		    float newPanelLeft = chartControl.CanvasLeft;
		    float newPanelTop = (float)chartScale.GetYByValue(chartScale.MaxValue);
		    float newPanelBottom = (float)chartScale.GetYByValue(chartScale.MinValue);
		
		    bool boundsChanged =
		        Math.Abs(newPanelRight - panelRight) > 0.01f ||
		        Math.Abs(newPanelLeft - panelLeft) > 0.01f ||
		        Math.Abs(newPanelTop - panelTop) > 0.01f ||
		        Math.Abs(newPanelBottom - panelBottom) > 0.01f;
		
		    if (boundsChanged)
		    {		
		        panelRight = newPanelRight;
		        panelLeft = newPanelLeft;
		        panelTop = newPanelTop;
		        panelBottom = newPanelBottom;
		    }		
		}
		
		private void UpdateFontAndMarkerMetrics(ChartControl chartControl)
		{
		    if (chartControl == null) return;
		
		    float baseFontSize = (float)(chartControl.Properties?.LabelFont?.Size ?? BaseFontSize);
		    float targetFontSize = baseFontSize * Math.Max((float)activeFontScale, MinFontScale);
		
		    if (Math.Abs(lastAppliedFontSize - targetFontSize) <= 0.1f) return;
			
	        averageEndMarkerX = 0.0f;

		    RebuildTextFormat(chartControl);		
		    FindMarkerMetrics(fontSizeRatio);			
		}
		
		private void RebuildTextFormat(ChartControl chartControl)
		{
		    float baseFontSize = (float)(chartControl?.Properties?.LabelFont?.Size ?? BaseFontSize);
		    float targetFontSize = baseFontSize * Math.Max((float)activeFontScale, MinFontScale);
		
		    try { dxTextFormat?.Dispose(); } catch { /* ignore */ }
		    dxTextFormat = CreateDxTextFormat(targetFontSize);
		
		    fontSizeRatio = targetFontSize / BaseFontSize;
		    lastAppliedFontSize = targetFontSize;
		
		    if (textLayoutManager != null && dxTextFormat != null)
		        textLayoutManager.SetTextFormat(dxTextFormat);
		}		
									
		private SharpDX.DirectWrite.TextFormat CreateDxTextFormat(float fontSize)
		{
		    string familyName = "Segoe UI";
		    try
		    {
		        var labelFont = ChartControl?.Properties?.LabelFont;
		        if (labelFont != null)
		            familyName = labelFont.Family.Source;
		    }
		    catch { /* ignore */ }
		
		    try
		    {
		        return new SharpDX.DirectWrite.TextFormat(
		            Core.Globals.DirectWriteFactory,
		            familyName,
		            null, // FontCollection
		            SharpDX.DirectWrite.FontWeight.Normal,
		            SharpDX.DirectWrite.FontStyle.Normal,
		            SharpDX.DirectWrite.FontStretch.Normal,
		            fontSize,
		            "en-us");
		    }
		    catch (Exception ex)
		    {
		        logger.Error($"StrategyIdentifier.CreateDxTextFormat error: {ex.Message}");
		        return null;
		    }
		}	
												
		private void FindMarkerMetrics(float fontSizeRatio)
		{
		    if (dxTextFormat == null) return;
		
		    try
		    {
		        using (var layout = new SharpDX.DirectWrite.TextLayout(
		            Core.Globals.DirectWriteFactory,
		            "Ay",
		            dxTextFormat,
		            100,
		            100))
		        {
		            heightMarkerRect = layout.Metrics.Height + MarkerHeightPadding;
		        }
		    }
		    catch { heightMarkerRect = (dxTextFormat?.FontSize ?? BaseFontSize) + MarkerHeightPadding; }
			
			offsetMarkerRect = heightMarkerRect * OffsetFactor;
			paddingMarkerRect = fontSizeRatio * PaddingSize;
		}								

		private void UpdateOrderDisplayBarLength(ChartTrader chartTrader)
		{
			if (chartTrader == null) return;			
			
		    int rawOrderDisplay = chartTrader.Properties?.OrderDisplayBarLength ?? 0;
		    float chartWidth = panelRight - panelLeft;
		
		    if (cachedOrderDisplayBar != rawOrderDisplay || cachedChartWidth != chartWidth)
		    {
		        cachedOrderDisplayBar = rawOrderDisplay;
		        cachedChartWidth = chartWidth;
		        orderDisplayBarPixelLength = rawOrderDisplay * chartWidth * 0.01f;
		    }
		}	
								
		#endregion							
        // ──────────────────────────────				
		#region DrawStrategyOrderLevelMarkers
			
		private void DrawStrategyOrderLevelMarkers(ChartScale chartScale, float markerHeight, float markerOffset, float markerPadding)
		{
		    if (RenderTarget == null || chartScale == null) return;
		
		    if (textDxBrush == null || borderDxBrush == null || markerBackDxBrush == null || upAmountBackDxBrush == null || downAmountBackDxBrush == null || 
		        entryBackDxBrush == null || targetBackDxBrush == null || stopBackDxBrush == null || upQuantityBackDxBrush == null || downQuantityBackDxBrush == null) return; 

		    if (markerSegmentRender == null || registry == null) return;
		
		    var merged = currentMergedSnapshot;
		    if (merged == null || merged.IsEmpty)
		    {
		        _ = RestoreZOrder();
		        return;
		    }

		    float lineWidth = HorizontalLineWidth;	
		    float markHeight = markerHeight;			
		    float padding = markerPadding;
		
		    bool isBold = IsBoldText;
		    float segmentGap = OrderLevelMarkerSegmentGap;
		
		    float halfHeight = markerHeight * 0.5f;
		    float radius = halfHeight;
		
		    bool isAggregated = false;
		
		    var strategies = merged.Strategies.Values
		        .Where(s => s != null)
		        .OrderBy(s => s.Fills?.LastOrDefault()?.TimeUtc ?? DateTime.MinValue)
		        .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
		        .ToList();
		
		    // PASS 1: Collect all the markers
		    var allMarkers = new List<LevelMarkerRender>();
		
		    foreach (var strat in strategies)
		    {
		        if (strat.Levels == null || strat.Levels.Count == 0) continue;
		
		        isAggregated = strat.IsAggregated;
		
		        foreach (var lvl in strat.Levels)
		        {
		            if (lvl == null) continue;
		
		            LevelMarkerRender marker = null;
		
		            if (lvl.HasEntries || !strat.HasPosition)
		                marker = BuildEntryMarker(lvl, strat, markHeight, isBold, radius, padding, isAggregated, segmentGap);
		            else if (lvl.HasTargets && strat.HasPosition)
		                marker = BuildOrderMarker(lvl, strat, markHeight, isBold, radius, padding, isAggregated, segmentGap, LevelMarkerType.Target);
		            else if (lvl.HasStops && strat.HasPosition)
		                marker = BuildOrderMarker(lvl, strat, markHeight, isBold, radius, padding, isAggregated, segmentGap, LevelMarkerType.Stop);
		
		            if (marker == null) continue;
		
		            marker.Y = (float)chartScale.GetYByValue(lvl.PriceLevel);
		            marker.PriceTickKey = tickSize > 0.0
		                ? PositionMath.PriceToTicks(lvl.PriceLevel, tickSize)
		                : (long)Math.Round(lvl.PriceLevel * 1_000_000.0, MidpointRounding.AwayFromZero);
		
		            allMarkers.Add(marker);									
		        }
		    } 
		
		    // PASS 2: Grouping and drawing
		    var groups = allMarkers
		        .GroupBy(m => m.PriceTickKey)
		        .OrderBy(g => g.First().Y)
		        .ToList();
		
		    bool hasOrders = groups.Count > 0;
		    bool hasPositions = strategies.Any(s => s.HasPosition);

			if (hasOrders || hasPositions)
			{
			    var mergedVersion = merged.Version;

			    if (lastZOrderSnapshotVersion != mergedVersion)
			    {
			        lastZOrderSnapshotVersion = mergedVersion;

			        previousZOrder = null;
			    }
			    
			    _ = BringToFrontZOrder();
			}
			else
			{
			    lastZOrderSnapshotVersion = -1;
			    _ = RestoreZOrder();
			}
		
		    int maxMarkCount = hasOrders ? groups.Max(g => g.Count()) : 0;

			float baseOffset = markerOffset; 
			float scaledOffset = baseOffset + baseOffset * maxMarkCount;
			
		    float markerDisplayBarPixelLength = isChartTraderAvailable 
				? orderDisplayBarPixelLength + scaledOffset + AddOrderMarkerOffset 
				: baseOffset + AddOrderMarkerOffset;
			
			float startMarkerX = ShowStratAvgPriceMarker && 
				averageEndMarkerX != 0.0f && 
				(averageEndMarkerX < panelRight - markerDisplayBarPixelLength) 
					? averageEndMarkerX - padding 
					: panelRight - markerDisplayBarPixelLength;

		    if (hasOrders) 
		    {
		        foreach (var group in groups)
		        {
		            var items = group
		                .OrderByDescending(m => m.Level.Orders?.FirstOrDefault()?.CreatedOrderTime ?? DateTime.MinValue)
		                .ToList();

				    var entryMarkers = items
				        .Where(m => m.MarkerType == LevelMarkerType.Entry)
				        .OrderByDescending(m => m.Level.Orders?.FirstOrDefault()?.CreatedOrderTime ?? DateTime.MinValue)
				        .ToList();
				    
					var otherMarkers = items
					    .Where(m => m.MarkerType != LevelMarkerType.Entry)
					    .OrderByDescending(m => m.Strategy?.FirstFillTimeUtc ?? DateTime.MinValue)
					    .ThenByDescending(m => m.Strategy?.Id ?? 0)
					    .ToList();

				    var orderedItems = entryMarkers.Concat(otherMarkers).ToList();
	
				    float totalLevelRowWidth = orderedItems.Sum(m => m.TotalWidth) + MarkerGroupGap * (orderedItems.Count - 1);								
				    float levelEndMarkerX = startMarkerX - totalLevelRowWidth;

				    float curX = levelEndMarkerX;
				    foreach (var item in orderedItems)
				    {
				        item.X = curX;
				        curX += item.TotalWidth + MarkerGroupGap;
				    }
				
				    float y = items[0].Y;

				    var lineBrush = ResolveGroupLineBrush(items);
				    v1.X = startMarkerX; v1.Y = y;
				    v2.X = panelRight; v2.Y = y;
				    RenderTarget.DrawLine(v1, v2, lineBrush, lineWidth);

				    foreach (var item in orderedItems)
				    {
				        float yRect = item.Y - halfHeight;
				        var rect = MakeRoundedRect(item.X, yRect, item.TotalWidth, markHeight, radius);
				
				        if (item.BackgroundBrush != null)
				            RenderTarget.FillRoundedRectangle(rect, item.BackgroundBrush);
				
				        float segX = item.X;
						for (int i = 0; i < item.Segments.Length; i++)
						{
						    var seg = item.Segments[i];
						    markerSegmentRender.DrawStrategySegment(segX, yRect, markHeight, seg);
						    segX += seg.Width + (i < item.Segments.Length - 1 ? segmentGap : 0.0f);
						}
				
				        WithAntialias(SharpDX.Direct2D1.AntialiasMode.PerPrimitive, () => RenderTarget.DrawRoundedRectangle(rect, borderDxBrush, 1.0f));	           
				    }
		        }
		    }
		
		    // PASS 3: Show the preview under the cursor
		    if (hasPositions || hasOrders)
		        DrawHoverStrategyPreview(chartScale, markHeight, halfHeight, padding, isBold, radius, lineWidth, startMarkerX, isAggregated, segmentGap);
		}
		
		#endregion		
        // ──────────────────────────────				
		#region DrawStrategyOrderLevelMarkers Builders
		
		private LevelMarkerRender BuildEntryMarker(StrategyPriceLevel lvl, StrategyModel strategy,
			float markHeight, bool isBold, float radius, float padding, bool isAggregated, float segmentGap)
		{
			string modeText = isAggregated ? "A" : "P";			
		    string strategyText = string.IsNullOrEmpty(strategy.Name) ?  "Unknown" : strategy.Name;
		    string orderText = GetLevelOrderText(lvl, out string actionText);
		    string qtyText = lvl.QuantityOrders.ToString();
			
		    var markerBgBrush = entryBackDxBrush;
			var modeBgBrush = markerBackDxBrush;
		    var orderBgBrush = actionText == "Buy" ? upAmountBackDxBrush : downAmountBackDxBrush;
		    var qtyBgBrush = actionText == "Buy" ? upQuantityBackDxBrush : downQuantityBackDxBrush;			

			var modeSeg = markerSegmentRender.BuildStrategySegment(modeText, padding, markHeight, isBold, modeBgBrush, textDxBrush, radius, borderDxBrush, brdWidth: 1.0f);			
		    var strategySeg = markerSegmentRender.BuildStrategySegment(strategyText, padding, markHeight, isBold, bgBrush: null, textDxBrush, radius, brdBrush: null, brdWidth: 0.0f);
		    var orderSeg = markerSegmentRender.BuildStrategySegment(orderText, padding, markHeight, isBold, orderBgBrush, textDxBrush, radius, borderDxBrush, brdWidth: 1.0f);				
		    var qtySeg = markerSegmentRender.BuildStrategySegment(qtyText, padding, markHeight, isBold, qtyBgBrush, textDxBrush, radius, borderDxBrush, brdWidth: 1.0f);
			
			orderSeg.Width = ApplyMinWidth(orderSeg.Width, markHeight);
			
		    var segmentList = new List<SegmentMarkerRender.MarkerStrategySegment>();
		    segmentList.Add(modeSeg);			
		    segmentList.Add(strategySeg);    
		    segmentList.Add(orderSeg);
		    segmentList.Add(qtySeg);		
		    var segments = segmentList.ToArray();
			
		    float totalWidth = segments.Sum(s => s.Width) + segmentGap * (segments.Length - 1);		
		
		    return new LevelMarkerRender
		    {
		        Strategy = strategy,
		        Level = lvl,
		        MarkerType = LevelMarkerType.Entry,
		        Segments = segments,
		        TotalWidth = totalWidth,
		        BackgroundBrush = markerBgBrush,
		        LineBrush = markerBgBrush
		    };
		}
		
		private LevelMarkerRender BuildOrderMarker(StrategyPriceLevel lvl, StrategyModel strategy,
		    float markHeight, bool isBold, float radius, float padding, bool isAggregated, float segmentGap, LevelMarkerType markerType)
		{
			bool isTarget = markerType == LevelMarkerType.Target;

		    string modeText = isAggregated ? "A" : "P";			
		    string strategyText = string.IsNullOrEmpty(strategy.Name) ? StrategyIdentity.UnknownStrategyName : strategy.Name;						
		    string formattedAmount = Math.Abs(lvl.Amount).ToString("N2", Core.Globals.GeneralOptions.CurrentCulture);
		    string sign = lvl.Amount > 0 ? "+" : lvl.Amount < 0 ? "-" : string.Empty;
		    string orderText = strategy.HasPosition ? $"{sign} {formattedAmount} $" : GetLevelOrderText(lvl);
		    string qtyText = lvl.QuantityOrders.ToString();
		
		    var markerBgBrush = isTarget ? targetBackDxBrush : stopBackDxBrush;
		    var modeBgBrush = markerBackDxBrush;
		    var orderBgBrush = lvl.Amount > 0 ? upAmountBackDxBrush : lvl.Amount < 0 ? downAmountBackDxBrush : entryBackDxBrush;
			var qtyBgBrush = isTarget ? upQuantityBackDxBrush : downQuantityBackDxBrush;

		    var modeSeg = markerSegmentRender.BuildStrategySegment(modeText, padding, markHeight, isBold, modeBgBrush, textDxBrush, radius, borderDxBrush, brdWidth: 1.0f);			
		    var strategySeg = markerSegmentRender.BuildStrategySegment(strategyText, padding, markHeight, isBold, bgBrush: null, textDxBrush, radius, brdBrush: null, brdWidth: 0.0f);
		    var orderSeg = markerSegmentRender.BuildStrategySegment(orderText, padding, markHeight, isBold, orderBgBrush, textDxBrush, radius, borderDxBrush, brdWidth: 1.0f);
		    var qtySeg = markerSegmentRender.BuildStrategySegment(qtyText, padding, markHeight, isBold, qtyBgBrush, textDxBrush, radius, borderDxBrush, brdWidth: 1.0f);
			
		    orderSeg.Width = ApplyMinWidth(orderSeg.Width, markHeight);
		
		    var segmentList = new List<SegmentMarkerRender.MarkerStrategySegment>();
		    segmentList.Add(modeSeg);			
		    segmentList.Add(strategySeg);    
		    segmentList.Add(orderSeg);
		    segmentList.Add(qtySeg);		
		    var segments = segmentList.ToArray();
			
		    float totalWidth = segments.Sum(s => s.Width) + segmentGap * (segments.Length - 1);
		
		    return new LevelMarkerRender
		    {
		        Strategy = strategy,
		        Level = lvl,
		        MarkerType = markerType,
		        Segments = segments,
		        TotalWidth = totalWidth,
		        BackgroundBrush = markerBgBrush,
		        LineBrush = markerBgBrush
		    };
		}
		
		#endregion
        // ──────────────────────────────				
		#region DrawStrategyOrderLevelMarkers Helpers			

		private string GetLevelOrderText(StrategyPriceLevel lvl)
		{
		    if (lvl.Orders?.Count > 0)
		    {
		        var first = lvl.Orders.FirstOrDefault();
		        if (first != null)
		            return $"{GetOrderActionName(first.Action)}  {GetOrderTypeName(first.OrderType)}";
		    }
		    return string.Empty;
		}

		private string GetLevelOrderText(StrategyPriceLevel lvl, out string actionText)
		{
		    actionText = string.Empty;
		    if (lvl.Orders?.Count > 0)
		    {
		        var first = lvl.Orders.FirstOrDefault();
		        if (first != null)
		        {
		            actionText = GetOrderActionName(first.Action);
		            return $"{actionText}  {GetOrderTypeName(first.OrderType)}";
		        }
		    }
		    return string.Empty;
		}		
		
		private static string GetOrderTypeName(OrderType orderType)
		{
		    return orderType switch
		    {
		        OrderType.Limit => "LMT",
		        OrderType.Market => "MKT",
		        OrderType.MIT => "MIT",
		        OrderType.StopMarket => "STP",
		        OrderType.StopLimit => "SLM",
		        _ => string.Empty
		    };
		}
		
		private static string GetOrderActionName(OrderAction action)
		{
		    return action switch
		    {
		        OrderAction.Buy => "Buy",
		        OrderAction.Sell => "Sell",
		        OrderAction.BuyToCover => "Buy",
		        OrderAction.SellShort => "Sell",				
		        _ => string.Empty
		    };
		}
		
		private SharpDX.Direct2D1.Brush ResolveGroupLineBrush(List<LevelMarkerRender> group)
		{
		    var stop = group.FirstOrDefault(m => m.MarkerType == LevelMarkerType.Stop);
		    if (stop != null) return stop.LineBrush;
		
		    var target = group.FirstOrDefault(m => m.MarkerType == LevelMarkerType.Target);
		    if (target != null) return target.LineBrush;
		
		    return group[0].LineBrush;
		}
		
		#endregion
        // ──────────────────────────────				
		#region DrawHoverStrategyPreview			
					
		private void DrawHoverStrategyPreview(ChartScale chartScale, float markHeight, float halfHeight,
		    float padding, bool isBold, float radius, float lineWidth, float startMarkerX, bool isAggregated, float segmentGap)
		{
		    if (!isChartTraderAvailable || !isMouseInsideChartPanel || !isChartControlCursorHand) return;

			var nearest = nearestLevelInfo;
			if (!nearest.IsFound || nearest.IsAmbiguous) return;

		    double currentPrice = ResolveCurrentPrice(marketPrice, closePrice);		    
		    double hovPrice = hoverPrice;
		    double averagePrice = nearest.StrategyAveragePrice;		
					
			if (double.IsNaN(currentPrice) || currentPrice <= 0.0) return;
			if (double.IsNaN(hoverPrice)) return;

			if (!nearest.IsEntry)
			{
			    if (double.IsNaN(averagePrice) || averagePrice <= 0.0) return;
			    if (pointValue <= 0.0) return;
			}
			
		    bool isLong = nearest.IsLong;
		    bool isShort = nearest.IsShort;					
		    bool isTarget = nearest.IsTarget;
		    bool isStop = nearest.IsStop;
			bool isEntry = nearest.IsEntry;
		    int targetQuantity = nearest.TargetQuantity;
		    int stopQuantity = nearest.StopQuantity;
			int  entryQuantity = nearest.EntryQuantity;
			string entryActionText = nearest.EntryActionText;
			string entryTypeText = nearest.EntryTypeText;			

			if (!isEntry)
			{
			    if (isLong && isTarget && hoverPrice < currentPrice || isLong && isStop  && hoverPrice > currentPrice || 			       
			        isShort && isTarget && hoverPrice > currentPrice || isShort && isStop  && hoverPrice < currentPrice) return;			       
			}

		    string modeText = isAggregated ? "A" : "P";			
		    string strategyText = string.IsNullOrEmpty(nearest.StrategyName) ? "Unknown" : nearest.StrategyName;					
		    string orderText = isEntry
				? string.IsNullOrEmpty(entryActionText) ? string.Empty : $"{entryActionText}  {entryTypeText}"
				: CalculateAmountForLevel(hoverPrice, averagePrice, isLong, isShort, targetQuantity, stopQuantity, isTarget, isStop, pointValue); 
			string qtyText = isEntry ? entryQuantity.ToString() : (isTarget ? targetQuantity.ToString() : stopQuantity.ToString());
			
		    var markerBgBrush = markerBackDxBrush;	
		    var lineBrush = borderDxBrush;			
		    var modeBgBrush = markerBackDxBrush;
			var orderBgBrush = isEntry
			    ? (entryActionText == "Buy" ? upAmountBackDxBrush : downAmountBackDxBrush)
			    : GetColorAmountForLevel(hoverPrice, averagePrice, isLong, isShort, targetQuantity, stopQuantity, isTarget, isStop, pointValue);

		    var modeSeg = markerSegmentRender.BuildStrategySegment(modeText, padding, markHeight, isBold, modeBgBrush, textDxBrush, radius, borderDxBrush, brdWidth: 1.0f);				
		    var strategySeg = markerSegmentRender.BuildStrategySegment(strategyText, padding, markHeight, isBold, bgBrush: null, textDxBrush, radius, brdBrush: null, brdWidth: 0.0f);			
		    var orderSeg = markerSegmentRender.BuildStrategySegment(orderText, padding, markHeight, isBold, orderBgBrush, textDxBrush, radius, borderDxBrush, brdWidth: 1.0f);			
		    var qtySeg = markerSegmentRender.BuildStrategySegment(qtyText, padding, markHeight, isBold, bgBrush: null, textDxBrush, radius, borderDxBrush, brdWidth: 1.0f);	

		    orderSeg.Width = ApplyMinWidth(orderSeg.Width, markHeight);
					
			var segmentList = new List<SegmentMarkerRender.MarkerStrategySegment>();
			segmentList.Add(modeSeg);				
			segmentList.Add(strategySeg);		
			segmentList.Add(orderSeg);
			segmentList.Add(qtySeg);

			var segments = segmentList.ToArray();
		
		    float totalLevelRowWidth = segments.Sum(s => s.Width) + segmentGap * (segments.Length - 1);					
		    float levelEndMarkerX = startMarkerX - totalLevelRowWidth;	
		
		    float hov = hoverY;
		    float yRect = hov - halfHeight;
		
		    v1.Y = hov; v1.X = startMarkerX;
		    v2.Y = hov; v2.X = panelRight;
		    var rect = MakeRoundedRect(levelEndMarkerX, yRect, totalLevelRowWidth, markHeight, radius);
		
		    RenderTarget.DrawLine(v1, v2, lineBrush, lineWidth);
		    RenderTarget.FillRoundedRectangle(rect, markerBgBrush);

		    float currentX = levelEndMarkerX;
		    for (int i = 0; i < segments.Length; i++)
		    {
		        markerSegmentRender.DrawStrategySegment(currentX, yRect, markHeight, segments[i]);
		        currentX += segments[i].Width + (i < segments.Length - 1 ? segmentGap : 0.0f);
		    }
		
		    WithAntialias(SharpDX.Direct2D1.AntialiasMode.PerPrimitive, () => RenderTarget.DrawRoundedRectangle(rect, borderDxBrush, 1.0f));       
		}
		
		#endregion			
        // ──────────────────────────────				
		#region DrawHoverStrategyPreview Helpers			
			
		private string CalculateAmountForLevel(double hoverPrice, double averagePrice,
		    bool isLong, bool isShort,int targetQuantity, int stopQuantity, bool isTarget, bool isStop, double pointValue)
		{
		    double rounded = GetLevelAmount(hoverPrice, averagePrice, isLong, isShort, targetQuantity, stopQuantity, isTarget, isStop, pointValue);
			
		    if (Math.Abs(rounded) < 0.01) return "0.00 $";  
			
		    string formatted = Math.Abs(rounded).ToString("N2", Core.Globals.GeneralOptions.CurrentCulture);
		    string sign = rounded > 0 ? "+" : rounded < 0 ? "-" : "";
			
		    return $"{sign} {formatted} $";
		}
				
		private SharpDX.Direct2D1.Brush GetColorAmountForLevel(double hoverPrice, double averagePrice,
		    bool isLong, bool isShort, int targetQuantity, int stopQuantity, bool isTarget, bool isStop, double pointValue)
		{
		    double rounded = GetLevelAmount(hoverPrice, averagePrice, isLong, isShort, targetQuantity, stopQuantity, isTarget, isStop, pointValue);
		
		    if (Math.Abs(rounded) < 0.01) return entryBackDxBrush;
			
		    return rounded > 0 ? upAmountBackDxBrush : rounded < 0 ? downAmountBackDxBrush : entryBackDxBrush;   
		}	

		private double GetLevelAmount(double hoverPrice, double averagePrice,
		    bool isLong, bool isShort, int targetQuantity, int stopQuantity, bool isTarget, bool isStop, double pointValue)
		{
		    double profitPerUnit = (hoverPrice - averagePrice) * (isLong ? 1.0 : isShort ? -1.0 : 0.0);

		    double amount = 0.0;
		
		    if (isTarget)
		        amount = profitPerUnit * targetQuantity * pointValue;
		    else if (isStop)
		        amount = profitPerUnit * stopQuantity * pointValue;
		
		    return Math.Round(amount, 2, MidpointRounding.AwayFromZero);
		}			

		#endregion	
        // ──────────────────────────────				
		#region DrawStrategyAveragePriceMarker

		private sealed class AverageMarkerGroupRender
		{
			public List<AverageMarkerRender> Items;
			public float LineStartX;
			public float LineY;
			public SharpDX.Direct2D1.Brush LineBrush;
		}

		private void DrawStrategyAveragePriceMarker(ChartScale chartScale, float markerHeight, float markerOffset, float markerPadding)
		{
			pendingAverageMarkerGroups.Clear();

		    if (!ShowStratAvgPriceMarker) return;

		    if (RenderTarget == null || chartScale == null) return;

		    if (textDxBrush == null || borderDxBrush == null || markerBackDxBrush == null || upAmountBackDxBrush == null || downAmountBackDxBrush == null || 
				targetBackDxBrush == null || stopBackDxBrush == null || upQuantityBackDxBrush == null || downQuantityBackDxBrush == null) return;

			if (markerSegmentRender == null || registry == null) return;
			
			var merged = currentMergedSnapshot;
		    if (merged == null || merged.IsEmpty) return;

		    float lineWidth = HorizontalLineWidth;	
			float markHeight = markerHeight;		
		    float padding = markerPadding;
			
		    bool isBold = IsBoldText;
			float segmentGap = AverageMarkerSegmentGap;
			
		    float radius = markHeight * 0.5f;
			
			float qtySegWidth = 0.0f;
			float maxAverageRowWidth = 0.0f;
			float newEndMarkerX = 0.0f;

			bool isLong = false;
			bool isShort = false;
			
			var strategies = merged.Strategies.Values
			    .Where(s => s != null && s.Position != null && s.Position.HasPosition)
			    .OrderBy(s => s.Fills?.LastOrDefault()?.TimeUtc ?? DateTime.MinValue)
			    .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
			    .ToList();
		
		    // PASS 1: Build markers
		    var allAverageMarkers = new List<AverageMarkerRender>();
		
		    foreach (var strat in strategies)
		    {
		        var pos = strat.Position;
		        if (pos == null || !pos.HasPosition) continue;
		
		        double averagePrice = pos.AveragePrice;
		        if (double.IsNaN(averagePrice) || averagePrice <= 0.0) continue;
		
		        float yAvg = (float)chartScale.GetYByValue(averagePrice);
		        if (float.IsNaN(yAvg) || float.IsInfinity(yAvg)) continue;
		
				bool isAggregated = strat.IsAggregated;
				int posQuantity = pos.QuantityPositions;

				if (isAggregated)
				{
				    isLong = pos.IsLong;
				    isShort = pos.IsShort;
				}
				else
				{
				    var positionStates = registry?.GetPositionStatesSnapshot(currentAccount, currentInstrument);
				    PositionState ps = null;
				    positionStates?.TryGetValue(strat.Id, out ps);
				    isLong = ps != null ? ps.Direction == MarketPosition.Long : pos.IsLong;
				    isShort = ps != null ? ps.Direction == MarketPosition.Short : pos.IsShort;
				}
		
		        var directionBrush = isLong ? targetBackDxBrush : stopBackDxBrush;

		        var renderSegs = new List<(SegmentMarkerRender.MarkerStrategySegment Seg, SharpDX.Direct2D1.Brush Brush)>();
	                                  		
		        // modeSeg
				string modeText = isAggregated ? "A" : "P";
		        var modeBgBrush = markerBackDxBrush;						
		        var modeSeg = markerSegmentRender.BuildStrategySegment(modeText, padding, markHeight, isBold, modeBgBrush, textDxBrush, radius: 0.0f, borderDxBrush, brdWidth: 1.0f);		            		           
		        renderSegs.Add((modeSeg, null));

		        // strategySeg		
		        if (isAggregated && strat.AggregatedStrategyInfos != null && strat.AggregatedStrategyInfos.Count > 0)
		        {
		            // Aggregated stratSeg
		            foreach (var h in strat.AggregatedStrategyInfos)
		            {
						string stAggText = h.Name;						
		                var stAggBgBrush = h.Direction == MarketPosition.Long ? targetBackDxBrush : stopBackDxBrush;
		                var stratAggSeg = markerSegmentRender.BuildStrategySegment(stAggText, padding, markHeight, isBold, stAggBgBrush, textDxBrush, radius: 0.0f, brdBrush: null, brdWidth: 0.0f);		                    
		                renderSegs.Add((stratAggSeg, null));
		            }
		        }
		        else
		        {
		            // Personal stratSeg
					string stPerText = string.IsNullOrEmpty(strat.AverageDisplayName) ? StrategyIdentity.UnknownStrategyName : strat.AverageDisplayName;
		            var stratPerSeg = markerSegmentRender.BuildStrategySegment(stPerText, padding, markHeight, isBold, directionBrush, textDxBrush, radius: 0.0f, brdBrush: null, brdWidth: 0.0f);  
		            renderSegs.Add((stratPerSeg, null));
		        }

		        // avgPriceSeg - optional
		        if (ShowAvgPrice)
		        {
					string priceText = FormatPrice(averagePrice);
		            var avgPriceSeg = markerSegmentRender.BuildStrategySegment(priceText, padding, markHeight, isBold, bgBrush: null, textDxBrush, radius: 0.0f, borderDxBrush, brdWidth: 1.0f);		                
		            renderSegs.Add((avgPriceSeg, null));
		        }
				
		        // unPnlSeg - optional
		        if (ShowUnPnl)
		        {
		            double currentPriceForUnPnl = GetCurrentPriceForUnPnl(marketPrice, closePrice, bidPrice, askPrice, isLong, isShort);
		            double unPnl = CalculateUnrealizedPnl(averagePrice, currentPriceForUnPnl, posQuantity, isLong, isShort, pointValue);
					string unPnlText = GetUnrealizedPnlText(unPnl);
		            var unPnlBgBrush = unPnl > 0 ? upAmountBackDxBrush : unPnl < 0 ? downAmountBackDxBrush : null;
		
		            var unPnlSeg = markerSegmentRender.BuildStrategySegment(unPnlText, padding, markHeight, isBold, unPnlBgBrush, textDxBrush, radius: 0.0f, borderDxBrush, brdWidth: 1.0f);		                		
		            var v = unPnlSeg; 
					v.Width = ApplyMinWidth(v.Width, markHeight);		           
		            renderSegs.Add((v, null));
		        }
		
		        // qtySeg - last
				string qtyText = posQuantity.ToString();
				var qtyBgBrush = posQuantity == 0 ? markerBackDxBrush : isLong ? upQuantityBackDxBrush : downQuantityBackDxBrush;
		        var qtySeg = markerSegmentRender.BuildStrategySegment(qtyText, padding, markHeight, isBold, qtyBgBrush, textDxBrush, radius: 0.0f, borderDxBrush, brdWidth: 1.0f);		            
		        renderSegs.Add((qtySeg, null));
				qtySegWidth = qtySeg.Width;

		        float total = renderSegs.Sum(r => r.Seg.Width) + segmentGap * (renderSegs.Count - 1);

		        long priceTickKey = tickSize > 0.0 ? PositionMath.PriceToTicks(averagePrice, tickSize) : (long)Math.Round(averagePrice * 1_000_000.0, MidpointRounding.AwayFromZero);

		        allAverageMarkers.Add(new AverageMarkerRender
		        {
		            Strategy = strat,
		            Position = pos,
		            AveragePrice = averagePrice,
		            Y = yAvg,
		            PriceTickKey = priceTickKey,
		            Segments = renderSegs.Select(r => r.Seg).ToArray(),
		            TotalWidth = total,
		            MarkerBgBrush = modeBgBrush,
		            LineBrush = directionBrush,
		            IsAggregated = isAggregated
		        });
		    }
		
			// PASS 2: Group by price tick, position, draw 
			var groups = allAverageMarkers
			    .GroupBy(m => m.PriceTickKey)
			    .OrderBy(g => g.First().Y)
			    .ToList();

		    if (groups.Count == 0)				
			{ 
				averageEndMarkerX = 0.0f; 
				return; 
			}				

			float baseOffset = markerOffset * 2 + qtySegWidth;
		    float markerDisplayBarPixelLength = isChartTraderAvailable 
				? orderDisplayBarPixelLength + baseOffset + AddAverageMarkerOffset 
				: padding + AddAverageMarkerOffset;
			float startMarkerX = panelRight - markerDisplayBarPixelLength;

			foreach (var group in groups)
			{
			    var items = group
			        .OrderByDescending(m => m.Strategy?.FirstFillTimeUtc ?? DateTime.MinValue)
			        .ThenByDescending(m => m.Strategy?.Id ?? 0)
			        .ToList();
			
			    if (items.All(m => m.IsAggregated))
			        items = items.Take(1).ToList();
			
			    float totalAverageRowWidth = items.Sum(m => m.TotalWidth) + MarkerGroupGap * (items.Count - 1);
			    float endMarkerX = startMarkerX - totalAverageRowWidth;
				
		        if (totalAverageRowWidth > maxAverageRowWidth)
		        {
		            maxAverageRowWidth = totalAverageRowWidth;
		            newEndMarkerX = endMarkerX;
		        }			
							
		        float curX = endMarkerX;
		        foreach (var item in items)
		        {
		            item.X = curX;
		            curX += item.TotalWidth + MarkerGroupGap;
		        }
		
		        float y = items[0].Y;
		        var lineBrush = items[0].LineBrush;
		
		        pendingAverageMarkerGroups.Add(new AverageMarkerGroupRender
		        {
		            Items = items,
		            LineStartX = startMarkerX,
		            LineY = y,
		            LineBrush = lineBrush
		        });
		    }
			
			ApplyAverageEndMarkerX(newEndMarkerX);
		
			pendingAverageMarkHeight = markHeight;
			pendingAverageSegmentGap = segmentGap;
		}

		private void RenderPendingAveragePriceMarkers()
		{
		    if (RenderTarget == null || pendingAverageMarkerGroups.Count == 0) return;

		    float markHeight = pendingAverageMarkHeight;
		    float segmentGap = pendingAverageSegmentGap;
			float lineWidth = HorizontalLineWidth;

		    foreach (var group in pendingAverageMarkerGroups)
		    {
		        v1.X = group.LineStartX; v1.Y = group.LineY;
		        v2.X = panelRight; v2.Y = group.LineY;
		        RenderTarget.DrawLine(v1, v2, group.LineBrush, lineWidth);

		        foreach (var item in group.Items)
		        {
		            float yRect = item.Y - markHeight * 0.5f;
		            var rect = MakeRoundedRect(item.X, yRect, item.TotalWidth, markHeight, radius: 0.0f);

		            if (item.MarkerBgBrush != null)
		                RenderTarget.FillRoundedRectangle(rect, item.MarkerBgBrush);

		            float currentX = item.X;
		            for (int i = 0; i < item.Segments.Length; i++)
		            {
		                markerSegmentRender.DrawStrategySegment(currentX, yRect, markHeight, item.Segments[i]);
		                currentX += item.Segments[i].Width + (i < item.Segments.Length - 1 ? segmentGap : 0.0f);
		            }

		            WithAntialias(SharpDX.Direct2D1.AntialiasMode.PerPrimitive, () => RenderTarget.DrawRoundedRectangle(rect, borderDxBrush, 1.0f));
		        }
		    }
		}

		#endregion
        // ──────────────────────────────			
		#region DrawStrategyAveragePriceMarker Helpers
			
		private double CalculateUnrealizedPnl(double averagePrice, double currentPrice, int posQuantity, bool isLong, bool isShort, double pointValue)
		{
		    if (double.IsNaN(currentPrice) || currentPrice <= 0.0 || double.IsNaN(averagePrice) || averagePrice <= 0.0 || pointValue <= 0.0) return 0.0; 

		    double diff = 0.0;
		    
		    if (isLong)
		        diff = currentPrice - averagePrice;
		    else if (isShort)
		        diff = averagePrice - currentPrice;
		    else
		        return 0.0;
		
		    return diff * posQuantity * pointValue;
		}
		
		private string GetUnrealizedPnlText(double unPnl)
		{
		    string formatted = Math.Abs(unPnl).ToString("N2", Core.Globals.GeneralOptions.CurrentCulture);
		    string sign = unPnl > 0 ? "+" : unPnl < 0 ? "-" : "";
		
		    return $"{sign} {formatted} $";
		}
		
		private void ApplyAverageEndMarkerX(float value)
		{
		    const float Epsilon = 0.5f;
		    if (Math.Abs(averageEndMarkerX - value) > Epsilon)
		        averageEndMarkerX = value;
		}

		#endregion	
        // ──────────────────────────────	
		#region Rendering Helpers

		private float ApplyMinWidth(float width, float height)
		{
		    return Math.Max(width, height * MinWidthFactor);
		}		
	
		private async Task BringToFrontZOrder()
		{
		    if (previousZOrder.HasValue) return; 
		
		    previousZOrder = ZOrder;
		
		    PostToUiThread (() =>
		    {
		        SetZOrder(int.MaxValue);
		    });
		}
		
		private async Task RestoreZOrder()
		{
		    if (!previousZOrder.HasValue) return;
		
		    int value = previousZOrder.Value;
		    previousZOrder = null;
		    
		    PostToUiThread (() =>
		    {
		        SetZOrder(value);
		    });		
		}		

		private void RequestRender()
		{
		    if (ChartControl == null) return;

		    if (Interlocked.CompareExchange(ref pendingRenderInvalidate, 1, 0) == 1) return;

		    PostToUiThread (() =>
		    {
		        try { if (ChartControl != null) ChartControl.InvalidateVisual(); }
		        finally { Volatile.Write(ref pendingRenderInvalidate, 0); }
		    }, DispatcherPriority.Render);
		}
		
		private SharpDX.Direct2D1.RoundedRectangle MakeRoundedRect(float left, float top, float width, float height, float radius)
		{
		    reusableRoundedRect.Rect = new SharpDX.RectangleF(left, top, width, height);
		    reusableRoundedRect.RadiusX = radius;
		    reusableRoundedRect.RadiusY = radius;
		    return reusableRoundedRect;
		}		

		void WithAntialias(SharpDX.Direct2D1.AntialiasMode mode, Action draw)
		{
		    var old = RenderTarget.AntialiasMode;
		    try { RenderTarget.AntialiasMode = mode; draw(); }
		    finally { RenderTarget.AntialiasMode = old; } 
		}

		#endregion		
        // ──────────────────────────────
		#region Initialize/Cleanup RenderResources

		public override void OnRenderTargetChanged()
		{
		    base.OnRenderTargetChanged();
		
		    try
		    {				
		        try { brushManager?.SetRenderTarget(RenderTarget); }         
		        catch (Exception ex) { logger.Error($"StrategyIdentifier.OnRenderTargetChanged: brushManager error: {ex.Message}"); }
		
		        if (RenderTarget == null)
		        {
		            try { dxTextFormat?.Dispose(); } catch { /* ignore */ }
		            dxTextFormat = null;
		            markerSegmentRender = null; 
		            return;
		        }

		        try
		        {
					RebuildTextFormat(ChartControl);
					FindMarkerMetrics(fontSizeRatio);
		        }
		        catch (Exception ex) 
				{ 
					dxTextFormat = null; 
					logger.Error($"StrategyIdentifier.OnRenderTargetChanged: dxTextFormat guard error: {ex.Message}"); 
				}	

		        try
		        {
		            brushManager?.SetRenderTarget(RenderTarget);
		
					if (brushManager != null)
					{
					    textDxBrush = brushManager.GetCachedDxBrush(BrushKey.Text);
					    borderDxBrush = brushManager.GetCachedDxBrush(BrushKey.Border);				
						markerBackDxBrush = brushManager.GetCachedDxBrush(BrushKey.BackMarker);						
					    upAmountBackDxBrush = brushManager.GetCachedDxBrush(BrushKey.BackAmountUp);
					    downAmountBackDxBrush = brushManager.GetCachedDxBrush(BrushKey.BackAmountDown);
					    entryBackDxBrush = brushManager.GetCachedDxBrush(BrushKey.BackEntry);
					    targetBackDxBrush = brushManager.GetCachedDxBrush(BrushKey.BackTarget);
					    stopBackDxBrush = brushManager.GetCachedDxBrush(BrushKey.BackStop);
					    upQuantityBackDxBrush = brushManager.GetCachedDxBrush(BrushKey.BackQuantityUp);
					    downQuantityBackDxBrush = brushManager.GetCachedDxBrush(BrushKey.BackQuantityDown);						
					}
		        }
		        catch (Exception ex) { logger.Error($"StrategyIdentifier.OnRenderTargetChanged: brush cache error: {ex.Message}"); }

				try
				{
				    if (textLayoutManager == null)
				        textLayoutManager = new TextLayoutDXManager(Core.Globals.DirectWriteFactory);
				    
				    if (dxTextFormat != null) 
				        textLayoutManager.SetTextFormat(dxTextFormat);
				    
				    textLayoutManager.Clear();
				}
				catch (Exception ex) { logger.Error($"StrategyIdentifier.OnRenderTargetChanged: textLayoutManager error: {ex.Message}"); }

		        try { if (RenderTarget != null && textLayoutManager != null) markerSegmentRender = new SegmentMarkerRender(RenderTarget, textLayoutManager); }  
		        catch (Exception ex) { logger.Error($"StrategyIdentifier.OnRenderTargetChanged: markerSegmentRender error: {ex.Message}"); }			
		    }
		    catch (Exception ex) { logger.Error($"StrategyIdentifier.OnRenderTargetChanged error: {ex.Message}"); }	
		}
				
		private void InitiateRenderResources()
		{
		    try
		    {
		        var brushes = new Dictionary<BrushKey, (Brush, double)>
		        {
		            { BrushKey.Text, (BrushText, TextOpacity) },
		            { BrushKey.Border, (BrushBorder, BorderOpacity) },
		            { BrushKey.BackMarker, (BrushBackMarker, BackgroundOpacity) },					
		            { BrushKey.BackAmountUp, (BrushBackAmountUp, BackgroundOpacity) },
		            { BrushKey.BackAmountDown, (BrushBackAmountDown, BackgroundOpacity) },
		            { BrushKey.BackEntry, (BrushBackEntry, BackgroundOpacity) },
		            { BrushKey.BackTarget, (BrushBackTarget, BackgroundOpacity) },
		            { BrushKey.BackStop, (BrushBackStop, BackgroundOpacity) },
		            { BrushKey.BackQuantityUp, (BrushBackQuantityUp, BackgroundOpacity) },
		            { BrushKey.BackQuantityDown, (BrushBackQuantityDown, BackgroundOpacity) }
		        };
				
				brushManager = new SharpDXBrushManager<BrushKey>(brushes);
				
				textLayoutManager = new TextLayoutDXManager(Core.Globals.DirectWriteFactory);
				
				if (RenderTarget != null)
			        OnRenderTargetChanged();	
				
				chartScale = ChartPanel?.Scales.FirstOrDefault(s => s.ScaleJustification == ScaleJustification);				
				UpdatePanelBounds(ChartControl, chartScale);
				
				_ = BringToFrontZOrder();
		    }
		    catch (Exception ex) { logger.Error($"StrategyIdentifier.InitiateRenderResources error: {ex.Message}"); brushManager = null; }	
		}		
						
		private void CleanupRenderResources()
		{
			try 
			{ 
		        ResetInteractionState();
				
				try { markerSegmentRender = null; } catch { /* ignore */ }
				markerSegmentRender = null;
				
			    try { dxTextFormat?.Dispose(); } catch { /* ignore */ }
			    dxTextFormat = null;
			
			    try { textLayoutManager?.Dispose(); } catch { /* ignore */ }
			    textLayoutManager = null;
						
			    try { brushManager?.Dispose(); } catch { /* ignore */ }
			    brushManager = null;
				
				_ = RestoreZOrder();
			}
		    catch (Exception ex) { logger.Error($"StrategyIdentifier.CleanupRenderResources error: {ex.Message}"); }
		}	

		private void ResetInteractionState()
		{
		    chartHoverTracker?.ResetInteractionState();
		    Interlocked.Exchange(ref pendingRenderInvalidate, 0);
		}		
	
		#endregion
        // ──────────────────────────────		
		#endregion
        // ==========================================================		
		#region NearestStrategyData
        // ──────────────────────────────		
		#region GetNearestStrategyDataOnMouseDown
						
		private void GetNearestStrategyDataOnMouseDown(double hoverPrice)
		{
			var positionStates = registry?.GetPositionStatesSnapshot(currentAccount, currentInstrument);
			chartHoverTracker?.ResolveNearestLevelOnMouseDown(currentMergedSnapshot, tickSize, positionStates);
		}

		private void ClearNearestStrategyDataForced() => chartHoverTracker?.ClearForced();		

		private void ClearNearestStrategyData() => chartHoverTracker?.ClearIfAllowed(currentMergedSnapshot);

		#endregion		
        // ──────────────────────────────							
		#region UpdateChartCursorState

		private void UpdateChartCursorState()
		{
			if (ChartControl == null) return;
			chartHoverTracker.UpdateCursorState(ChartControl.Cursor);
		}

		#endregion				
        // ──────────────────────────────
		#region Mouse Handlers

		private void ChartControl_MouseMove(object sender, MouseEventArgs e)
		{
		    if (ChartControl == null || chartScale == null) return; 
			
			UpdateChartCursorState();		

		    var pos = e.GetPosition(ChartControl);	

			chartHoverTracker?.OnMouseMove((float)pos.X, (float)pos.Y, panelLeft, panelRight, panelTop, panelBottom, 
				chartScale, tickSize, currentMergedSnapshot, PositionMath.SnapToTickStatic);		
		}

        private void ChartControl_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
			if (ChartControl == null || chartScale == null) return;
			if (double.IsNaN(hoverPrice) || !isMouseInsideChartPanel) return;

		    GetNearestStrategyDataOnMouseDown(hoverPrice);							
        }

		private void ChartControl_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
		{
			if (ChartControl == null) return;			
			ClearNearestStrategyData();
		}
					
		#endregion						
        // ──────────────────────────────				
		#region Attach/Detach Mouse Handlers
									
		private void AttachMouseHandlers()
		{
		    try
		    {
		        if (isMouseHandlersAttached) return;
				
				if (ChartControl != null)				
				{			
			        ChartControl.MouseMove += ChartControl_MouseMove;
			        ChartControl.PreviewMouseLeftButtonDown += ChartControl_PreviewMouseLeftButtonDown;
					ChartControl.PreviewMouseLeftButtonUp += ChartControl_PreviewMouseLeftButtonUp;
				}		
				
		        isMouseHandlersAttached = true;
		    }
		    catch (Exception ex) { logger.Error($"StrategyIdentifier.AttachMouseHandlers error: {ex.Message}"); }
		}		
						
		private void DetachMouseHandlers()
		{
		    try
		    {
		        if (!isMouseHandlersAttached) return;
				
				if (ChartControl != null)				
				{		
				    ChartControl.MouseMove -= ChartControl_MouseMove;
				    ChartControl.PreviewMouseLeftButtonDown -= ChartControl_PreviewMouseLeftButtonDown;
					ChartControl.PreviewMouseLeftButtonUp -= ChartControl_PreviewMouseLeftButtonUp;
				}

		        isMouseHandlersAttached = false;
		    }
		    catch (Exception ex) { logger.Error($"StrategyIdentifier.DetachMouseHandlers error: {ex.Message}"); }
		}		

		#endregion
        // ──────────────────────────────		
		#endregion		
        // ==========================================================
		#region User Properties
        // ──────────────────────────────
        #region Strategy Mode Settings

		[NinjaScriptProperty]
		[Display(Name = "Calculation Mode (P) / (A)", GroupName = "[1] Strategy State", Order = 1, Description = "")]			
		public StrategyCalculationMode StrategyMode
		{
		    get { return strategyMode; }
		    set
		    {
		        if (strategyMode == value) return;
		        
		        strategyMode = value;
				logger?.Debug($"StrategyIdentifier.StrategyMode changed to {value}");

				if (ValidateAccount(currentAccount) && ValidateInstrument(currentInstrument))
				{
				    var key = new SlotKey(currentAccount, currentInstrument);
				    MergedStrategySnapshot snap;
				    if (!snapshotCache.TryGetValue((key, value), out snap) || snap == null)
				        snap = registry?.GetMergedSnapshot(currentAccount, currentInstrument, value);

				    currentMergedSnapshot = snap ?? MergedStrategySnapshot.Empty;
				    ClearNearestStrategyDataForced();
				}
		    }
		}		

        #endregion	
        // ──────────────────────────────		
        #region Display Markers Settings

        [NinjaScriptProperty]
        [Display(Name = "Show Average Marker", GroupName = "[2] Display Markers", Order = 1, Description = "")]					
        public bool ShowStratAvgPriceMarker { get; set; }
		
        [NinjaScriptProperty]
        [Display(Name = "Show Average Price", GroupName = "[2] Display Markers", Order = 2, Description = "")]					
        public bool ShowAvgPrice { get; set; }		
		
        [NinjaScriptProperty]
        [Display(Name = "Show UnPnl Strategy", GroupName = "[2] Display Markers", Order = 3, Description = "")]					
        public bool ShowUnPnl { get; set; }

        [NinjaScriptProperty]
        [Range(-5000, 5000)]
        [Display(Name = "Order Offset", GroupName = "[2] Display Markers", Order = 4, Description = "")]			           
        public float AddOrderMarkerOffset { get; set; }	
		
        [NinjaScriptProperty]
        [Range(-5000, 5000)]
        [Display(Name = "Average Offset", GroupName = "[2] Display Markers", Order = 5, Description = "")]			           
        public float AddAverageMarkerOffset { get; set; }
		
        [NinjaScriptProperty]
        [Range(0, 100)]
        [Display(Name = "Line Width", GroupName = "[2] Display Markers", Order = 6, Description = "")]			           
        public float HorizontalLineWidth { get; set; }			

        [NinjaScriptProperty]
        [Display(Name = "Bold Text", GroupName = "[2] Display Markers", Order = 7, Description = "")]					
        public bool IsBoldText { get; set; }			

        #endregion
		
        // ──────────────────────────────
        #region Brush Settings

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Text", GroupName = "[3] Brushes", Order = 1, Description = "")] 
        public Brush BrushText { get; set; }
		[Browsable(false)]
		public string BrushTextSerializable
		{
			get { return Serialize.BrushToString(BrushText); }
			set { BrushText = Serialize.StringToBrush(value); }
		}		

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Border", GroupName = "[3] Brushes", Order = 2, Description = "")] 
        public Brush BrushBorder { get; set; }
		[Browsable(false)]
		public string BrushBorderSerializable
		{
			get { return Serialize.BrushToString(BrushBorder); }
			set { BrushBorder = Serialize.StringToBrush(value); }
		}		

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Marker Background", GroupName = "[3] Brushes", Order = 3, Description = "")] 
        public Brush BrushBackMarker { get; set; }
		[Browsable(false)]
		public string BrushBackMarkerSerializable
		{
			get { return Serialize.BrushToString(BrushBackMarker); }
			set { BrushBackMarker = Serialize.StringToBrush(value); }
		}		

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Positive Amount", GroupName = "[3] Brushes", Order = 4, Description = "")] 
        public Brush BrushBackAmountUp { get; set; }
		[Browsable(false)]
		public string BrushBackAmountUpSerializable
		{
			get { return Serialize.BrushToString(BrushBackAmountUp); }
			set { BrushBackAmountUp = Serialize.StringToBrush(value); }
		}
		
        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Negative Amount", GroupName = "[3] Brushes", Order = 5, Description = "")] 
        public Brush BrushBackAmountDown { get; set; }
		[Browsable(false)]
		public string BrushBackAmountDownSerializable
		{
			get { return Serialize.BrushToString(BrushBackAmountDown); }
			set { BrushBackAmountDown = Serialize.StringToBrush(value); }
		}
		
        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Entry", GroupName = "[3] Brushes", Order = 6, Description = "")] 
        public Brush BrushBackEntry { get; set; }
		[Browsable(false)]
		public string BrushBackEntrySerializable
		{
			get { return Serialize.BrushToString(BrushBackEntry); }
			set { BrushBackEntry = Serialize.StringToBrush(value); }
		}
		
        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Target", GroupName = "[3] Brushes", Order = 7, Description = "")] 
        public Brush BrushBackTarget { get; set; }
		[Browsable(false)]
		public string BrushBackTargetSerializable
		{
			get { return Serialize.BrushToString(BrushBackTarget); }
			set { BrushBackTarget = Serialize.StringToBrush(value); }
		}		

        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Stop", GroupName = "[3] Brushes", Order = 8, Description = "")] 
        public Brush BrushBackStop { get; set; }
		[Browsable(false)]
		public string BrushBackStopSerializable
		{
			get { return Serialize.BrushToString(BrushBackStop); }
			set { BrushBackStop = Serialize.StringToBrush(value); }
		}
		
        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Up Quantity", GroupName = "[3] Brushes", Order = 9, Description = "")] 
        public Brush BrushBackQuantityUp { get; set; }
		[Browsable(false)]
		public string BrushBackQuantityUpSerializable
		{
			get { return Serialize.BrushToString(BrushBackQuantityUp); }
			set { BrushBackQuantityUp = Serialize.StringToBrush(value); }
		}
		
        [NinjaScriptProperty]
        [XmlIgnore]
        [Display(Name = "Down Quantity", GroupName = "[3] Brushes", Order = 10, Description = "")] 
        public Brush BrushBackQuantityDown { get; set; }
		[Browsable(false)]
		public string BrushBackQuantityDownSerializable
		{
			get { return Serialize.BrushToString(BrushBackQuantityDown); }
			set { BrushBackQuantityDown = Serialize.StringToBrush(value); }
		}		

        #endregion
        // ──────────────────────────────		
        #region NinjaScript Order Names

		[NinjaScriptProperty]
		[Display(Name = "NS Stop Names (e.g. StopLoss, Stp, SL, ...)", GroupName = "[4] NinjaScript Strategy", Order = 1, Description = "")]		   
		public string NsStopSignalNames
		{
		    get => nsStopSignalNames;
		    set
		    {
		        nsStopSignalNames = value;
		        registry?.UpdateSignalNamesForAll(nsStopSignalNames, nsTargetSignalNames);
		    }
		}
		
		[NinjaScriptProperty]
		[Display(Name = "NS Target Names (e.g. TakeProfit, Trg, TP, ...)", GroupName = "[4] NinjaScript Strategy", Order = 2, Description = "")]		   
		public string NsTargetSignalNames
		{
		    get => nsTargetSignalNames;
		    set
		    {
		        nsTargetSignalNames = value;
		        registry?.UpdateSignalNamesForAll(nsStopSignalNames, nsTargetSignalNames);
		    }
		}
		
		#endregion			
        // ──────────────────────────────
        #region Logging Settings

        [NinjaScriptProperty]
        [Display(Name = "Enable Debug Logging", GroupName = "[5] Logging", Order = 1, Description = "")]					
        public bool EnableDebugLogging { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Log Output Tab", GroupName = "[5] Logging", Order = 2, Description = "")]					
        public PrintTo LogOutputTab { get; set; }
		
		[NinjaScriptProperty]
		[Display(Name = "Clear Output", GroupName = "[5] Logging", Order = 3, Description = "")]
		public bool IClearOutputWindow
		{ get; set; }		

        #endregion
        // ──────────────────────────────		
		#endregion				
        // ==========================================================		
	}							
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private StrategyIdentifier.StrategyIdentifier[] cacheStrategyIdentifier;
		public StrategyIdentifier.StrategyIdentifier StrategyIdentifier(StrategyCalculationMode strategyMode, bool showStratAvgPriceMarker, bool showAvgPrice, bool showUnPnl, float addOrderMarkerOffset, float addAverageMarkerOffset, float horizontalLineWidth, bool isBoldText, Brush brushText, Brush brushBorder, Brush brushBackMarker, Brush brushBackAmountUp, Brush brushBackAmountDown, Brush brushBackEntry, Brush brushBackTarget, Brush brushBackStop, Brush brushBackQuantityUp, Brush brushBackQuantityDown, string nsStopSignalNames, string nsTargetSignalNames, bool enableDebugLogging, PrintTo logOutputTab, bool iClearOutputWindow)
		{
			return StrategyIdentifier(Input, strategyMode, showStratAvgPriceMarker, showAvgPrice, showUnPnl, addOrderMarkerOffset, addAverageMarkerOffset, horizontalLineWidth, isBoldText, brushText, brushBorder, brushBackMarker, brushBackAmountUp, brushBackAmountDown, brushBackEntry, brushBackTarget, brushBackStop, brushBackQuantityUp, brushBackQuantityDown, nsStopSignalNames, nsTargetSignalNames, enableDebugLogging, logOutputTab, iClearOutputWindow);
		}

		public StrategyIdentifier.StrategyIdentifier StrategyIdentifier(ISeries<double> input, StrategyCalculationMode strategyMode, bool showStratAvgPriceMarker, bool showAvgPrice, bool showUnPnl, float addOrderMarkerOffset, float addAverageMarkerOffset, float horizontalLineWidth, bool isBoldText, Brush brushText, Brush brushBorder, Brush brushBackMarker, Brush brushBackAmountUp, Brush brushBackAmountDown, Brush brushBackEntry, Brush brushBackTarget, Brush brushBackStop, Brush brushBackQuantityUp, Brush brushBackQuantityDown, string nsStopSignalNames, string nsTargetSignalNames, bool enableDebugLogging, PrintTo logOutputTab, bool iClearOutputWindow)
		{
			if (cacheStrategyIdentifier != null)
				for (int idx = 0; idx < cacheStrategyIdentifier.Length; idx++)
					if (cacheStrategyIdentifier[idx] != null && cacheStrategyIdentifier[idx].StrategyMode == strategyMode && cacheStrategyIdentifier[idx].ShowStratAvgPriceMarker == showStratAvgPriceMarker && cacheStrategyIdentifier[idx].ShowAvgPrice == showAvgPrice && cacheStrategyIdentifier[idx].ShowUnPnl == showUnPnl && cacheStrategyIdentifier[idx].AddOrderMarkerOffset == addOrderMarkerOffset && cacheStrategyIdentifier[idx].AddAverageMarkerOffset == addAverageMarkerOffset && cacheStrategyIdentifier[idx].HorizontalLineWidth == horizontalLineWidth && cacheStrategyIdentifier[idx].IsBoldText == isBoldText && cacheStrategyIdentifier[idx].BrushText == brushText && cacheStrategyIdentifier[idx].BrushBorder == brushBorder && cacheStrategyIdentifier[idx].BrushBackMarker == brushBackMarker && cacheStrategyIdentifier[idx].BrushBackAmountUp == brushBackAmountUp && cacheStrategyIdentifier[idx].BrushBackAmountDown == brushBackAmountDown && cacheStrategyIdentifier[idx].BrushBackEntry == brushBackEntry && cacheStrategyIdentifier[idx].BrushBackTarget == brushBackTarget && cacheStrategyIdentifier[idx].BrushBackStop == brushBackStop && cacheStrategyIdentifier[idx].BrushBackQuantityUp == brushBackQuantityUp && cacheStrategyIdentifier[idx].BrushBackQuantityDown == brushBackQuantityDown && cacheStrategyIdentifier[idx].NsStopSignalNames == nsStopSignalNames && cacheStrategyIdentifier[idx].NsTargetSignalNames == nsTargetSignalNames && cacheStrategyIdentifier[idx].EnableDebugLogging == enableDebugLogging && cacheStrategyIdentifier[idx].LogOutputTab == logOutputTab && cacheStrategyIdentifier[idx].IClearOutputWindow == iClearOutputWindow && cacheStrategyIdentifier[idx].EqualsInput(input))
						return cacheStrategyIdentifier[idx];
			return CacheIndicator<StrategyIdentifier.StrategyIdentifier>(new StrategyIdentifier.StrategyIdentifier(){ StrategyMode = strategyMode, ShowStratAvgPriceMarker = showStratAvgPriceMarker, ShowAvgPrice = showAvgPrice, ShowUnPnl = showUnPnl, AddOrderMarkerOffset = addOrderMarkerOffset, AddAverageMarkerOffset = addAverageMarkerOffset, HorizontalLineWidth = horizontalLineWidth, IsBoldText = isBoldText, BrushText = brushText, BrushBorder = brushBorder, BrushBackMarker = brushBackMarker, BrushBackAmountUp = brushBackAmountUp, BrushBackAmountDown = brushBackAmountDown, BrushBackEntry = brushBackEntry, BrushBackTarget = brushBackTarget, BrushBackStop = brushBackStop, BrushBackQuantityUp = brushBackQuantityUp, BrushBackQuantityDown = brushBackQuantityDown, NsStopSignalNames = nsStopSignalNames, NsTargetSignalNames = nsTargetSignalNames, EnableDebugLogging = enableDebugLogging, LogOutputTab = logOutputTab, IClearOutputWindow = iClearOutputWindow }, input, ref cacheStrategyIdentifier);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.StrategyIdentifier.StrategyIdentifier StrategyIdentifier(StrategyCalculationMode strategyMode, bool showStratAvgPriceMarker, bool showAvgPrice, bool showUnPnl, float addOrderMarkerOffset, float addAverageMarkerOffset, float horizontalLineWidth, bool isBoldText, Brush brushText, Brush brushBorder, Brush brushBackMarker, Brush brushBackAmountUp, Brush brushBackAmountDown, Brush brushBackEntry, Brush brushBackTarget, Brush brushBackStop, Brush brushBackQuantityUp, Brush brushBackQuantityDown, string nsStopSignalNames, string nsTargetSignalNames, bool enableDebugLogging, PrintTo logOutputTab, bool iClearOutputWindow)
		{
			return indicator.StrategyIdentifier(Input, strategyMode, showStratAvgPriceMarker, showAvgPrice, showUnPnl, addOrderMarkerOffset, addAverageMarkerOffset, horizontalLineWidth, isBoldText, brushText, brushBorder, brushBackMarker, brushBackAmountUp, brushBackAmountDown, brushBackEntry, brushBackTarget, brushBackStop, brushBackQuantityUp, brushBackQuantityDown, nsStopSignalNames, nsTargetSignalNames, enableDebugLogging, logOutputTab, iClearOutputWindow);
		}

		public Indicators.StrategyIdentifier.StrategyIdentifier StrategyIdentifier(ISeries<double> input , StrategyCalculationMode strategyMode, bool showStratAvgPriceMarker, bool showAvgPrice, bool showUnPnl, float addOrderMarkerOffset, float addAverageMarkerOffset, float horizontalLineWidth, bool isBoldText, Brush brushText, Brush brushBorder, Brush brushBackMarker, Brush brushBackAmountUp, Brush brushBackAmountDown, Brush brushBackEntry, Brush brushBackTarget, Brush brushBackStop, Brush brushBackQuantityUp, Brush brushBackQuantityDown, string nsStopSignalNames, string nsTargetSignalNames, bool enableDebugLogging, PrintTo logOutputTab, bool iClearOutputWindow)
		{
			return indicator.StrategyIdentifier(input, strategyMode, showStratAvgPriceMarker, showAvgPrice, showUnPnl, addOrderMarkerOffset, addAverageMarkerOffset, horizontalLineWidth, isBoldText, brushText, brushBorder, brushBackMarker, brushBackAmountUp, brushBackAmountDown, brushBackEntry, brushBackTarget, brushBackStop, brushBackQuantityUp, brushBackQuantityDown, nsStopSignalNames, nsTargetSignalNames, enableDebugLogging, logOutputTab, iClearOutputWindow);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.StrategyIdentifier.StrategyIdentifier StrategyIdentifier(StrategyCalculationMode strategyMode, bool showStratAvgPriceMarker, bool showAvgPrice, bool showUnPnl, float addOrderMarkerOffset, float addAverageMarkerOffset, float horizontalLineWidth, bool isBoldText, Brush brushText, Brush brushBorder, Brush brushBackMarker, Brush brushBackAmountUp, Brush brushBackAmountDown, Brush brushBackEntry, Brush brushBackTarget, Brush brushBackStop, Brush brushBackQuantityUp, Brush brushBackQuantityDown, string nsStopSignalNames, string nsTargetSignalNames, bool enableDebugLogging, PrintTo logOutputTab, bool iClearOutputWindow)
		{
			return indicator.StrategyIdentifier(Input, strategyMode, showStratAvgPriceMarker, showAvgPrice, showUnPnl, addOrderMarkerOffset, addAverageMarkerOffset, horizontalLineWidth, isBoldText, brushText, brushBorder, brushBackMarker, brushBackAmountUp, brushBackAmountDown, brushBackEntry, brushBackTarget, brushBackStop, brushBackQuantityUp, brushBackQuantityDown, nsStopSignalNames, nsTargetSignalNames, enableDebugLogging, logOutputTab, iClearOutputWindow);
		}

		public Indicators.StrategyIdentifier.StrategyIdentifier StrategyIdentifier(ISeries<double> input , StrategyCalculationMode strategyMode, bool showStratAvgPriceMarker, bool showAvgPrice, bool showUnPnl, float addOrderMarkerOffset, float addAverageMarkerOffset, float horizontalLineWidth, bool isBoldText, Brush brushText, Brush brushBorder, Brush brushBackMarker, Brush brushBackAmountUp, Brush brushBackAmountDown, Brush brushBackEntry, Brush brushBackTarget, Brush brushBackStop, Brush brushBackQuantityUp, Brush brushBackQuantityDown, string nsStopSignalNames, string nsTargetSignalNames, bool enableDebugLogging, PrintTo logOutputTab, bool iClearOutputWindow)
		{
			return indicator.StrategyIdentifier(input, strategyMode, showStratAvgPriceMarker, showAvgPrice, showUnPnl, addOrderMarkerOffset, addAverageMarkerOffset, horizontalLineWidth, isBoldText, brushText, brushBorder, brushBackMarker, brushBackAmountUp, brushBackAmountDown, brushBackEntry, brushBackTarget, brushBackStop, brushBackQuantityUp, brushBackQuantityDown, nsStopSignalNames, nsTargetSignalNames, enableDebugLogging, logOutputTab, iClearOutputWindow);
		}
	}
}

#endregion
