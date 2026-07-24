#region Using declarations
using System;
using System.Collections.Generic;
using System.Threading;     
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;  
using System.Windows.Automation.Provider;                  
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.NinjaScript;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.Gui.NinjaScript.AtmStrategy;
#endregion

using NinjaTrader.NinjaScript.AddOns.AddOnsStrategyIdentifier.NtNullLogger;

//This namespace holds Add ons in this folder and is required. Do not change it. 
namespace NinjaTrader.NinjaScript.AddOns.AddOnsStrategyIdentifier.ChartTraderService
{
    // ==========================================================
    #region IChartTraderService
	
    public interface IChartTraderService : IDisposable
    {
        bool IsChartTraderAvailable { get; }
        Account CurrentAccount { get; }
        Instrument CurrentInstrument { get; }		

        Task InitializeAsync(Chart chartWindow, ChartTrader chartTrader);
        void Cleanup();
        event Action<ChartTraderState> StateChanged;
    }

    #endregion	
    // ==========================================================
    #region ChartTraderState
	
    public class ChartTraderState
    {
        public bool IsAvailable { get; set; }		
        public Account Account { get; set; }
        public Instrument Instrument { get; set; }		
    }

    #endregion	
    // ==========================================================
    #region ChartTraderService
		
    public class ChartTraderService : IChartTraderService
    {	
        private readonly Func<Action, DispatcherPriority, Task> _runOnUiThreadAsync;
        private readonly Action<Action, DispatcherPriority> _invokeOnUiThread;
        private readonly Action<Action, DispatcherPriority> _postToUiThread ;
		private readonly IStrategyLogger _logger;
		
        // - ChartTrader Controls - 
        private Chart _chartWindow;			
        private ChartTrader _chartTrader;
        private Grid _chartTraderGrid;
        private AccountSelector _chartTraderAccountSelector;
        private ComboBox _chartTraderInstrumentComboBox;		

        // - Condition - 	
        private bool _isChartTraderAvailable;
        private bool _isNoChartTraderControlsFound;		
        private bool _isSyncChartTraderEventsSubscribed;
        private bool _isSyncControlsFromChartTrader;
		private int _isInitializing;		
        private int _isSyncSource;
        private bool _isDisposed;
		
        // - Constants - 		
		private const int MaxNumberAttempts = 5;
		private const int SearchDelayMs = 300;		

        // - Public properties - 
        public bool IsChartTraderAvailable => _isChartTraderAvailable;		
        public Account CurrentAccount { get; private set; }
        public Instrument CurrentInstrument { get; private set; }	

        public event Action<ChartTraderState> StateChanged;
	    // ──────────────────────────────	
		#region Constructor	

        public ChartTraderService(
            Func<Action, DispatcherPriority, Task> runOnUiThreadAsync,
            Action<Action, DispatcherPriority> invokeOnUiThread,
            Action<Action, DispatcherPriority> postToUiThread,
			IStrategyLogger logger)
        {
			_runOnUiThreadAsync = runOnUiThreadAsync ?? throw new ArgumentNullException(nameof(runOnUiThreadAsync));
			_invokeOnUiThread = invokeOnUiThread ?? throw new ArgumentNullException(nameof(invokeOnUiThread));
			_postToUiThread = postToUiThread ?? throw new ArgumentNullException(nameof(postToUiThread));
			_logger = logger ?? NullLogger.Instance;
        }
	
		#endregion	
	    // ──────────────────────────────
		#region Initialization		

		public async Task InitializeAsync(Chart chartWindow, ChartTrader chartTrader)
		{
			if (_isDisposed) return;
		    if (Interlocked.CompareExchange(ref _isInitializing, 1, 0) == 1) return;

		    try
		    {
			    _chartWindow = chartWindow;

		        if (!ReferenceEquals(_chartTrader, chartTrader))
		        {
		            if (_chartTrader != null)
		                _chartTrader.IsVisibleChanged -= ChartTrader_IsVisibleChanged;
		
		            _chartTrader = chartTrader;
		        }

		        if (_chartTrader != null)
		        {
		            _chartTrader.IsVisibleChanged -= ChartTrader_IsVisibleChanged;
		            _chartTrader.IsVisibleChanged += ChartTrader_IsVisibleChanged;
		        }

		        UnSubscribeChartTraderEvents();

		        CheckChartTraderState(_chartTrader);
		
		        if (!_isChartTraderAvailable)
		        {
		            _isNoChartTraderControlsFound = true;
		            return;
		        }

		        bool found = await FindChartTraderControlsAsync(_chartWindow, _chartTrader);
		
		        if (!found)
		        {
		            _isNoChartTraderControlsFound = true;
		            return;
		        }

		        SyncControlsFromChartTrader();

		        SubscribeChartTraderEvents();
		
		        _isNoChartTraderControlsFound = false;
		    }
		    catch (Exception ex) { _logger.Error($"ChartTraderService.InitializeAsync error: {ex.Message}"); }
		    finally { Interlocked.Exchange(ref _isInitializing, 0); }
		}
	
		#endregion	
	    // ──────────────────────────────
		#region Control Search			

        private async Task<bool> FindChartTraderControlsAsync(Chart chartWindow, ChartTrader chartTrader)
        {
            if (chartWindow == null || chartTrader == null) return false;

            try
            {				
                _logger.Debug("ChartTraderService.FindChartTraderControlsAsync: Searching for ChartTrader controls...");
				
			    for (int attempt = 1; attempt <= MaxNumberAttempts; attempt++)
			    {
	                await _runOnUiThreadAsync(async () =>
	                {
	                    var chartTraderControl = chartWindow.FindFirst("ChartWindowChartTraderControl") as ChartTrader;
	                    if (chartTraderControl == null) { _logger.Debug("ChartTraderService.FindChartTraderControlsAsync: ChartTrader control not found"); _chartTraderGrid = null; }
	                    else { _chartTraderGrid = chartTraderControl.Content as Grid; }

	                    _chartTraderAccountSelector = 
							FindNameControl<AccountSelector>("cbxAccounts") ?? _chartTraderGrid?.FindFirst("ChartTraderControlAccountSelector") as AccountSelector;	
	                    _chartTraderInstrumentComboBox = 
							FindNameControl<ComboBox>("cbxInstruments") ?? _chartTraderGrid?.FindFirst("ChartTraderControlInstrumentSelector") as ComboBox;					
	                }, DispatcherPriority.Normal);

					bool success = AllChartTraderControlsFound() && AreControlsAlive();

	                if (success)
	                {
		                PrintControlSearchResults();					
	                    _logger.Debug("ChartTraderService.FindChartTraderControlsAsync: All ChartTrader controls found successfully");
	                    return true;
	                }
			
			        await Task.Delay(SearchDelayMs);										
			    }

				PrintControlSearchResults();
                _logger.Debug("ChartTraderService.FindChartTraderControlsAsync: Not all ChartTrader controls were found");
				return false;			
            }
            catch (Exception ex) { _logger.Error($"ChartTraderService.FindChartTraderControlsAsync error: {ex.Message}"); }

            return false;
        }

        private T FindNameControl<T>(string name) where T : FrameworkElement => _chartTrader?.FindName(name) as T;
	
		#endregion	
	    // ──────────────────────────────
		#region Synchronization			

        private void SyncControlsFromChartTrader()
        {
            if (_isSyncControlsFromChartTrader) return;

            _postToUiThread (() =>
            {
                try
                {
                    _isSyncControlsFromChartTrader = true;

                    if (AllChartTraderControlsFound())
                    {
                        SyncAccountFromChartTrader();						
                        SyncInstrumentFromChartTrader();						
                    }
                }
                catch (Exception ex) { _logger.Error($"ChartTraderService.SyncControlsFromChartTrader error: {ex.Message}"); }
                finally { _isSyncControlsFromChartTrader = false; }
            }, DispatcherPriority.Normal);			
        }

		private void SyncAccountFromChartTrader()
		{
		    RunChartTraderSync(() =>
		    {
		        SyncValue(
		            getSource: () => _chartTrader?.Account,
		            setTarget: acc =>
		            {
		                CurrentAccount = acc;
		                _logger.Debug($"ChartTraderService.SyncAccountFromChartTrader: Account={acc?.Name}");
		            },
		            validate: ValidateAccount,
		            name: "Account"
		        );		
		    }); 
		}

        private void SyncInstrumentFromChartTrader()
        {
            RunChartTraderSync(() =>
            {
                SyncValue(
                    getSource: () => _chartTrader?.Instrument,
                    setTarget: instr =>
                    {
                        CurrentInstrument = instr;
                        _logger.Debug($"ChartTraderService.SyncInstrumentFromChartTrader: Instrument={instr?.FullName}");
                    },
                    validate: ValidateInstrument,
                    name: "Instrument"
                );
            }); 
        }		
	
		#endregion	
	    // ──────────────────────────────
		#region Subscriptions			

        private void SubscribeChartTraderEvents()
        {
            if (_isSyncChartTraderEventsSubscribed) return;

            if (_chartTraderAccountSelector != null)
                _chartTraderAccountSelector.SelectionChanged += _chartTraderAccountSelector_SelectionChanged;
            if (_chartTraderInstrumentComboBox != null)
                _chartTraderInstrumentComboBox.SelectionChanged += _chartTraderInstrumentComboBox_SelectionChanged;
			
            _isSyncChartTraderEventsSubscribed = true;
        }

        private void UnSubscribeChartTraderEvents()
        {
            if (!_isSyncChartTraderEventsSubscribed) return;

            try
            {
                if (_chartTraderAccountSelector != null)
                    _chartTraderAccountSelector.SelectionChanged -= _chartTraderAccountSelector_SelectionChanged;
                if (_chartTraderInstrumentComboBox != null)
                    _chartTraderInstrumentComboBox.SelectionChanged -= _chartTraderInstrumentComboBox_SelectionChanged;			
            }
            catch (Exception ex) { _logger.Error($"ChartTraderService.UnSubscribeChartTraderEvents error: {ex.Message}"); }

            _isSyncChartTraderEventsSubscribed = false;
        }
	
		#endregion	
	    // ──────────────────────────────
		#region Event handlers			

        private void ChartTrader_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is not bool visible) return;

            if (_isNoChartTraderControlsFound)
                _ = InitializeAsync(_chartWindow, _chartTrader);
            else
                CheckChartTraderState(_chartTrader);

            NotifyStateChanged();
        }

        private void _chartTraderAccountSelector_SelectionChanged(object s, SelectionChangedEventArgs e) => SyncAccountFromChartTrader();          
        private void _chartTraderInstrumentComboBox_SelectionChanged(object s, SelectionChangedEventArgs e) => SyncInstrumentFromChartTrader();    		
	
		#endregion	
	    // ──────────────────────────────
		#region Helper methods			

        private void CheckChartTraderState(ChartTrader chartTrader)
        {
            if (chartTrader == null) return;
            _isChartTraderAvailable = chartTrader.IsVisible;
            _logger.Debug($"ChartTraderService.CheckChartTraderState: ChartTrader Available={_isChartTraderAvailable}");
        }	

        private void RunChartTraderSync(Action action)
        {
            if (!_isChartTraderAvailable || _isDisposed) return;
            if (!TryEnterSyncFromChartTrader()) return;

            try { action(); }
            finally { ExitSync(); } 
        }

        private void SyncValue<T>(Func<T> getSource, Action<T> setTarget, Func<T, bool> validate = null, string name = null)
        {
            T value;
            try { value = getSource != null ? getSource() : default; }
            catch (Exception ex) { _logger.Error($"[{name}] getSource error: {ex.Message}"); return; }

            if (validate != null)
            {
				try { if (!validate(value)) { _logger.Debug($"ChartTraderService [{name}] validation failed"); return; } }
                catch (Exception ex) { _logger.Error($"[{name}] validation error: {ex.Message}"); return; }
            }

            _invokeOnUiThread(() =>
            {
                try { setTarget?.Invoke(value); }
                catch (Exception ex) { _logger.Error($"[{name}] setTarget error: {ex.Message}"); }
            }, DispatcherPriority.Normal);

            NotifyStateChanged();
        }

        private bool TryEnterSyncFromChartTrader() => Interlocked.CompareExchange(ref _isSyncSource, 1, 0) == 0;

        private void ExitSync() => Interlocked.Exchange(ref _isSyncSource, 0);

        private void NotifyStateChanged()
        {
            StateChanged?.Invoke(new ChartTraderState
            {
                IsAvailable = _isChartTraderAvailable,
                Account = CurrentAccount,
                Instrument = CurrentInstrument
            });
        }			

        private bool AllChartTraderControlsFound()
		{
		    return 
				_chartTraderAccountSelector != null && 
				_chartTraderInstrumentComboBox != null;					           		         
		}			

		private bool AreControlsAlive()
		{
		    return 
				IsControlAlive(_chartTraderAccountSelector) && 
				IsControlAlive(_chartTraderInstrumentComboBox);			       	       
		}		
		
		private bool IsControlAlive(FrameworkElement element)
		{
		    if (element == null) return false;

		    return element.IsLoaded && PresentationSource.FromVisual(element) != null;		          
		}		
		
        private void PrintControlSearchResults()
        {
            _logger.Debug("ChartTraderService.PrintControlSearchResults => ChartTrader controls:");
            _logger.Debug($"AccountSelector={_chartTraderAccountSelector != null}");
            _logger.Debug($"InstrumentComboBox={_chartTraderInstrumentComboBox != null}");				
        }
	
		#endregion	
	    // ──────────────────────────────
		#region Validation			

        private bool ValidateAccount(Account account)
        {
            try
            {
                if (account == null) return false;
                lock (Account.All) { return Account.All.Contains(account); }
            }
            catch (Exception ex) { _logger.Error($"ChartTraderService.ValidateAccount error: {ex.Message}"); return false; }
        }

        private bool ValidateInstrument(Instrument instrument)
        {
            try
            {
                return instrument != null &&
                       !string.IsNullOrWhiteSpace(instrument.FullName) &&
                       instrument.MasterInstrument != null;
            }
            catch (Exception ex) { _logger.Error($"ChartTraderService.ValidateInstrument error: {ex.Message}"); return false; }
        }	
	
		#endregion	
	    // ──────────────────────────────
		#region Cleanup / Dispose			

        public void Cleanup()
        {
            _invokeOnUiThread(() =>
            {			
	            try
	            {
	                if (_chartTrader != null) _chartTrader.IsVisibleChanged -= ChartTrader_IsVisibleChanged;
	
	                UnSubscribeChartTraderEvents();
					
					_isChartTraderAvailable = false;					
					_isNoChartTraderControlsFound = false;
					_isSyncControlsFromChartTrader = false;
					
	                _chartTraderAccountSelector = null;
	                _chartTraderInstrumentComboBox = null;				
	                _chartTraderGrid = null;
	                _chartTrader = null;
					_chartWindow = null;
	            }
	            catch (Exception ex) { _logger.Error($"ChartTraderService.Cleanup error: {ex.Message}"); }
            }, DispatcherPriority.Normal);
			_logger.Debug("ChartTraderService.Cleanup: Completed");			
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            Cleanup();
        }
	
		#endregion	
	    // ──────────────────────────────	
    }

    #endregion	
    // ==========================================================
}
