#region Using declarations
using System;
using System.Collections.Generic;
using System.Linq;
using NinjaTrader.Cbi;
#endregion

using NinjaTrader.NinjaScript.AddOns.AddOnsStrategyIdentifier.NtNullLogger;

//This namespace holds Add ons in this folder and is required. Do not change it.
namespace NinjaTrader.NinjaScript.AddOns.AddOnsStrategyIdentifier.StrategySnapshotManager
{
    // ==========================================================
    #region DebugSnapshotState

    internal static class DebugSnapshotState
    {
        public static void LogMergedSnapshot(
            IStrategyLogger logger,
            MergedStrategySnapshot merged,
            Account currentAccount,
            Instrument currentInstrument,
            Func<double, string> formatPrice)
        {
            logger = logger ?? NullLogger.Instance;
            formatPrice = formatPrice ?? (p => p.ToString("N2"));

            if (merged == null)
            {
                logger.Debug("");
                logger.Debug("StrategyIdentifier.DebugOnMergedSnapshotChanged: Merged Snapshot = null");
                return;
            }

            logger.Debug("");
            logger.Debug("StrategyIdentifier.DebugOnMergedSnapshotChanged: MERGED SNAPSHOT UPDATED =>");
            logger.Debug($"For current: Account={currentAccount?.Name ?? "null"} | Instrument={currentInstrument?.FullName ?? "null"}");
            logger.Debug($"SNAPSHOT VERSION = {merged.Version}");
            logger.Debug($"ACTIVE STRATEGIES = {merged.Strategies?.Count ?? 0}");

            if (merged.IsEmpty)
            {
                logger.Debug("");
                logger.Debug("StrategyIdentifier.DebugOnMergedSnapshotChanged: NO ACTIVE STRATEGIES");
                return;
            }

            foreach (var kv in merged.Strategies.OrderBy(x => x.Key.StrategyId))
            {
                var strategy = kv.Value;
                if (strategy == null) continue;

                logger.Debug("");
                logger.Debug($"StrategyIdentifier.DebugOnMergedSnapshotChanged:");
                logger.Debug($"STRATEGY NAME = {strategy.Name}");
                logger.Debug($"STRATEGY ID = {kv.Key.StrategyId}");
                logger.Debug($"HAS POSITION = {strategy.HasPosition}");
                logger.Debug($"MARKET POSITION = {strategy.Position.MarketPosition}");
                logger.Debug($"POSITION QTY = {strategy.QuantityPositions}");
                logger.Debug($"AVERAGE PRICE = {formatPrice(strategy.AveragePrice)}");
                logger.Debug($"LEVELS AVERAGE PRICE = {formatPrice(strategy.LevelsAveragePrice)}");
                logger.Debug($"ENTRY ORDERS = {strategy.Entries.Count}");
                logger.Debug($"STOP ORDERS = {strategy.Stops.Count}");
                logger.Debug($"TARGET ORDERS = {strategy.Targets.Count}");
                logger.Debug($"PRICE LEVELS = {strategy.Levels?.Count ?? 0}");
                logger.Debug($"RECENT FILLS = {strategy.Fills?.Count ?? 0}");

                // Entry orders
                foreach (var o in strategy.Entries)
                {
                    if (o == null) continue;
                    logger.Debug("");
                    logger.Debug($"StrategyIdentifier.DebugOnMergedSnapshotChanged:");
                    logger.Debug(
                        $"ENTRY ORDER => OrderId={o.OrderId} | Name={o.OrderName} | " +
                        $"Action={o.Action} | OrderType={o.OrderType} | " +
                        $"Qty={o.QuantityOrders} | Limit={formatPrice(o.LimitPrice)} | " +
                        $"Stop={formatPrice(o.StopPrice)} | State={o.State}");
                }

                // Stop orders
                foreach (var o in strategy.Stops)
                {
                    if (o == null) continue;
                    logger.Debug("");
                    logger.Debug($"StrategyIdentifier.DebugOnMergedSnapshotChanged:");
                    logger.Debug(
                        $"STOP ORDER => OrderId={o.OrderId} | Name={o.OrderName} | " +
                        $"Action={o.Action} | OrderType={o.OrderType} | " +
                        $"Qty={o.QuantityOrders} | StopPrice={formatPrice(o.StopPrice)} | State={o.State}");
                }

                // Target orders
                foreach (var o in strategy.Targets)
                {
                    if (o == null) continue;
                    logger.Debug("");
                    logger.Debug($"StrategyIdentifier.DebugOnMergedSnapshotChanged:");
                    logger.Debug(
                        $"TARGET ORDER => OrderId={o.OrderId} | Name={o.OrderName} | " +
                        $"Action={o.Action} | OrderType={o.OrderType} | " +
                        $"Qty={o.QuantityOrders} | LimitPrice={formatPrice(o.LimitPrice)} | State={o.State}");
                }

                // Levels
                if (strategy.Levels != null)
                {
                    foreach (var lvl in strategy.Levels)
                    {
                        if (lvl == null) continue;
                        logger.Debug("");
                        logger.Debug($"StrategyIdentifier.DebugOnMergedSnapshotChanged:");
                        logger.Debug(
                            $"LEVEL => Strategy={strategy.Name} | PriceLevel={formatPrice(lvl.PriceLevel)} | " +
                            $"Kind={lvl.Kind} | Amount={lvl.Amount:N2} | " +
                            $"TotalQty={lvl.QuantityOrders} | Orders={lvl.Orders?.Count ?? 0}");
                    }
                }

                // Fills (last 10)
                if (strategy.Fills != null && strategy.Fills.Count > 0)
                {
                    logger.Debug("");
                    logger.Debug($"StrategyIdentifier.DebugOnMergedSnapshotChanged:");
                    logger.Debug("FILLS (most recent first) => last 10");
                    foreach (var f in strategy.Fills.Reverse().Take(10))
                    {
                        if (f == null) continue;
                        logger.Debug(
                            $"FILL => Sequence={f.Sequence} | Time={f.TimeUtc:HH:mm:ss.fff} | " +
                            $"Action={f.Action} | Qty={f.QuantityOrders} | " +
                            $"Price={formatPrice(f.Price)} | OrderId={f.OrderId}");
                    }
                }
            }
        }

        public static void LogPositionStates(
            IStrategyLogger logger,
            IReadOnlyDictionary<long, PositionState> states,
            Func<double, string> formatPrice)
        {
            logger = logger ?? NullLogger.Instance;
            formatPrice = formatPrice ?? (p => p.ToString("N2"));

            if (states == null || states.Count == 0)
            {
                logger.Debug("");
                logger.Debug($"StrategyIdentifier.DebugPositionStates:");
                logger.Debug("POSITION STATES => none");
                return;
            }

            logger.Debug("");
            logger.Debug($"StrategyIdentifier.DebugPositionStates:");
            logger.Debug("POSITION STATES =>");

            foreach (var kv in states.OrderBy(x => x.Key.ToString()))
            {
                var id = kv.Key;
                var s = kv.Value;
                if (s == null) continue;

                logger.Debug(
                    $"Strategy={id} | Direction={s.Direction} | Qty={s.Quantity} | " +
                    $"Avg={formatPrice(s.AveragePrice)} | Lifecycle={s.LifecycleState} | " +
                    $"Initialized={s.IsInitialized}");
            }
        }
    }

    #endregion
    // ==========================================================	
}
