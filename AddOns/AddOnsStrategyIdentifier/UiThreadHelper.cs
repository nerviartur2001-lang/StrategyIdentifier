#region Using declarations
using System;
using System.Threading.Tasks;
using System.Windows.Threading;
#endregion

using NinjaTrader.NinjaScript.AddOns.AddOnsStrategyIdentifier.NtNullLogger;

//This namespace holds Add ons in this folder and is required. Do not change it.
namespace NinjaTrader.NinjaScript.AddOns.AddOnsStrategyIdentifier.UiThreadHelper
{
    // ==========================================================
    #region UiThreadHelper

    internal sealed class UiThreadHelper
    {
        private readonly Func<Dispatcher> _getDispatcher;
        private readonly IStrategyLogger _logger;
        private readonly string _ownerName;

        public UiThreadHelper(Func<Dispatcher> getDispatcher, IStrategyLogger logger, string ownerName)
        {
            _getDispatcher = getDispatcher ?? throw new ArgumentNullException(nameof(getDispatcher));
            _logger = logger ?? NullLogger.Instance;
            _ownerName = string.IsNullOrEmpty(ownerName) ? "UiThreadHelper" : ownerName;
        }

        public void InvokeOnUiThread(Action action, DispatcherPriority priority = DispatcherPriority.Normal)
        {
            if (action == null) return;

            var dispatcher = _getDispatcher();
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                try { action(); }
                catch (Exception ex) { _logger.Error($"{_ownerName}.InvokeOnUiThread direct error: {ex.Message}"); }
                return;
            }

            try { dispatcher.Invoke(action, priority); }
            catch (Exception ex) { _logger.Error($"{_ownerName}.InvokeOnUiThread dispatcher.Invoke error: {ex.Message}"); }
        }

        public void PostToUiThread(Action action, DispatcherPriority priority = DispatcherPriority.Normal)
        {
            if (action == null) return;

            var dispatcher = _getDispatcher();
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                try { action(); }
                catch (Exception ex) { _logger.Error($"{_ownerName}.PostToUiThread direct error: {ex.Message}"); }
                return;
            }

            try { dispatcher.BeginInvoke(action, priority); }
            catch (Exception ex) { _logger.Error($"{_ownerName}.PostToUiThread BeginInvoke error: {ex.Message}"); }
        }

        public Task RunOnUiThreadAsync(Func<Task> func, DispatcherPriority priority = DispatcherPriority.Normal)
        {
            if (func == null) return Task.CompletedTask;

            var dispatcher = _getDispatcher();

            if (dispatcher == null || dispatcher.CheckAccess())
            {
                try { return func(); }
                catch (Exception ex) { _logger.Error($"{_ownerName}.RunOnUiThreadAsync func error: {ex.Message}"); 
				return Task.FromException(ex); }
            }

            return dispatcher.InvokeAsync(func, priority).Task.Unwrap();
        }
    }

    #endregion
    // ==========================================================	
}
