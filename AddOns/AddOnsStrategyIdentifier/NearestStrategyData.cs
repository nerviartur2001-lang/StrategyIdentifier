#region Using declarations
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using NinjaTrader.Cbi;
using NinjaTrader.Gui.Chart;
#endregion

using NinjaTrader.NinjaScript.AddOns.AddOnsStrategyIdentifier.StrategySnapshotManager;

//This namespace holds Add ons in this folder and is required. Do not change it. 
namespace NinjaTrader.NinjaScript.AddOns.AddOnsStrategyIdentifier.NearestStrategyData
{
    // ==========================================================
	#region NearestLevelInfo	

	public sealed class NearestLevelInfo
	{
		public static readonly NearestLevelInfo Empty = new NearestLevelInfo();

		public string StrategyName { get; set; } = string.Empty;
		public long? StrategyId { get; set; }
		public double LevelPrice { get; set; } = double.NaN;
		public double StrategyAveragePrice { get; set; } = double.NaN;

		public bool IsLong { get; set; }
		public bool IsShort { get; set; }
		
		public bool IsTarget { get; set; }
		public bool IsStop { get; set; }
		public bool IsEntry { get; set; }
		
		public int OrdersQuantity { get; set; }
		public int TargetQuantity { get; set; }
		public int StopQuantity { get; set; }
		public int EntryQuantity { get; set; }

		public bool IsAmbiguous { get; set; }

		public string EntryActionText { get; set; } = string.Empty;
		public string EntryTypeText { get; set; } = string.Empty;

		public bool IsFound => StrategyId.HasValue;
	}
	
	#endregion	
    // ==========================================================
	#region NearestLevelResolver	

	internal sealed class NearestLevelResolver
	{
		private readonly Func<OrderAction, string> _getOrderActionName;
		private readonly Func<OrderType, string> _getOrderTypeName;

		public NearestLevelResolver(Func<OrderAction, string> getOrderActionName, Func<OrderType, string> getOrderTypeName)
		{
			_getOrderActionName = getOrderActionName ?? throw new ArgumentNullException(nameof(getOrderActionName));
			_getOrderTypeName = getOrderTypeName ?? throw new ArgumentNullException(nameof(getOrderTypeName));
		}

		public NearestLevelInfo Resolve(MergedStrategySnapshot merged,			
			double hoverPrice, double tickSize, IReadOnlyDictionary<long, PositionState> positionStatesSnapshot)
		{
			if (merged == null || merged.IsEmpty) return NearestLevelInfo.Empty;

			var (nearestStrategy, nearestLevel) = FindNearestStrategyLevel(merged, hoverPrice, tickSize);
			if (nearestStrategy == null || nearestLevel == null) return NearestLevelInfo.Empty;

			double levelPrice = nearestLevel.PriceLevel;

			bool isLong;
			bool isShort;
			if (nearestStrategy.IsAggregated)
			{
				if (positionStatesSnapshot != null && positionStatesSnapshot.TryGetValue(nearestStrategy.Id, out var ps))
				{
					isLong = ps.Direction == MarketPosition.Long;
					isShort = ps.Direction == MarketPosition.Short;
				}
				else
				{
					isLong = nearestStrategy.IsLong;
					isShort = nearestStrategy.IsShort;
				}
			}
			else
			{
				isLong = nearestStrategy.IsLong;
				isShort = nearestStrategy.IsShort;
			}

			List<StrategyOrderDto> ordersAtLevel = nearestLevel.Orders != null && nearestLevel.Orders.Count > 0
				? nearestLevel.Orders.Where(o => o != null).ToList()
				: GetStrategyOrdersAtLevel(nearestStrategy, levelPrice, tickSize);

			int totalQty = 0;
			int targetQty = 0;
			int stopQty = 0;
			int entryQty = 0;
			string entryActionText = string.Empty;
			string entryTypeText = string.Empty;

			foreach (var order in ordersAtLevel)
			{
				if (order == null) continue;

				int qty = Math.Abs(order.QuantityOrders);
				totalQty += qty;

				var effectiveCategory = nearestLevel.Kind;

				if ((effectiveCategory & StrategyOrderCategory.Target) != 0) targetQty += qty;
				if ((effectiveCategory & StrategyOrderCategory.Stop) != 0) stopQty += qty;
				if ((effectiveCategory & StrategyOrderCategory.Entry) != 0)
				{
					entryQty += qty;
					if (string.IsNullOrEmpty(entryActionText))
					{
						entryActionText = _getOrderActionName(order.Action);
						entryTypeText = _getOrderTypeName(order.OrderType);
					}
				}
			}

			int totalLevelsAtKey = 0;
			long hoverTickKey = PositionMath.PriceToTicks(levelPrice, tickSize);
			foreach (var strat in merged.Strategies.Values)
			{
				if (strat?.Levels == null) continue;
				foreach (var lvl in strat.Levels)
				{
					if (lvl == null) continue;
					if (PositionMath.PriceToTicks(lvl.PriceLevel, tickSize) == hoverTickKey)
						totalLevelsAtKey++;
				}
			}

			bool isAmbiguous = totalLevelsAtKey > 1 || ordersAtLevel.Count > 1;

			return new NearestLevelInfo
			{
				StrategyName = string.IsNullOrEmpty(nearestStrategy.Name) ? string.Empty : nearestStrategy.Name,
				StrategyId = nearestStrategy.Id,
				LevelPrice = levelPrice,
				StrategyAveragePrice = nearestStrategy.LevelsAveragePrice,
				IsLong = isLong,
				IsShort = isShort,
				OrdersQuantity = totalQty,
				TargetQuantity = targetQty,
				StopQuantity = stopQty,
				EntryQuantity = entryQty,
				IsTarget = targetQty > 0,
				IsStop = stopQty > 0,
				IsEntry = entryQty > 0,
				IsAmbiguous = isAmbiguous,
				EntryActionText = entryActionText,
				EntryTypeText = entryTypeText
			};
		}

		private static StrategyPriceLevel FindNearestLevelInSortedList(
			IReadOnlyList<StrategyPriceLevel> levels, double hoverPrice, double tickSize)
		{
			if (levels == null || levels.Count == 0) return null;

			int n = levels.Count;
			int low = 0, high = n - 1;
			while (low <= high)
			{
				int mid = (low + high) >> 1;
				double midPrice = levels[mid].PriceLevel;
				if (midPrice < hoverPrice) low = mid + 1;
				else high = mid - 1;
			}

			int idx1 = low < n ? low : -1;
			int idx0 = (low - 1) >= 0 ? (low - 1) : -1;

			if (tickSize > 0.0)
			{
				long hoverKey = PositionMath.PriceToTicks(hoverPrice, tickSize);
				StrategyPriceLevel best = null;
				long bestDist = long.MaxValue;

				if (idx0 >= 0)
				{
					long k0 = PositionMath.PriceToTicks(levels[idx0].PriceLevel, tickSize);
					long dist0 = Math.Abs(k0 - hoverKey);
					best = levels[idx0];
					bestDist = dist0;
				}

				if (idx1 >= 0)
				{
					long k1 = PositionMath.PriceToTicks(levels[idx1].PriceLevel, tickSize);
					long dist1 = Math.Abs(k1 - hoverKey);
					if (dist1 < bestDist)
					{
						best = levels[idx1];
						bestDist = dist1;
					}
				}

				return best;
			}
			else
			{
				const double EPS = 1e-12;
				StrategyPriceLevel best = null;
				double bestDist = double.MaxValue;
				if (idx0 >= 0)
				{
					double d0 = Math.Abs(levels[idx0].PriceLevel - hoverPrice);
					best = levels[idx0];
					bestDist = d0;
				}
				if (idx1 >= 0)
				{
					double d1 = Math.Abs(levels[idx1].PriceLevel - hoverPrice);
					if (d1 < bestDist - EPS)
					{
						best = levels[idx1];
						bestDist = d1;
					}
				}
				return best;
			}
		}

		private static (StrategyModel strategy, StrategyPriceLevel level) FindNearestStrategyLevel(
			MergedStrategySnapshot merged, double hoverPrice, double tickSize)
		{
			if (merged?.Strategies == null || merged.Strategies.Count == 0) return (null, null);

			double bestPriceDist = double.MaxValue;
			long bestTickDist = long.MaxValue;
			StrategyModel nearestStrategy = null;
			StrategyPriceLevel nearestLevel = null;

			bool useTicks = tickSize > 0.0;

			foreach (var strat in merged.Strategies.Values)
			{
				if (strat?.Levels == null || strat.Levels.Count == 0) continue;

				var candidate = FindNearestLevelInSortedList(strat.Levels, hoverPrice, tickSize);
				if (candidate == null) continue;

				if (useTicks)
				{
					long hoverKey = PositionMath.PriceToTicks(hoverPrice, tickSize);
					long lvlKey = PositionMath.PriceToTicks(candidate.PriceLevel, tickSize);
					long dist = Math.Abs(lvlKey - hoverKey);
					if (dist < bestTickDist)
					{
						bestTickDist = dist;
						nearestStrategy = strat;
						nearestLevel = candidate;
					}
				}
				else
				{
					double dist = Math.Abs(candidate.PriceLevel - hoverPrice);
					if (dist < bestPriceDist)
					{
						bestPriceDist = dist;
						nearestStrategy = strat;
						nearestLevel = candidate;
					}
				}
			}

			return (nearestStrategy, nearestLevel);
		}

		private static List<StrategyOrderDto> GetStrategyOrdersAtLevel(StrategyModel strategy, double levelPrice, double tickSize)
		{
			var result = new List<StrategyOrderDto>();
			if (strategy?.Levels == null || strategy.Levels.Count == 0) return result;

			if (tickSize > 0.0)
			{
				long key = PositionMath.PriceToTicks(levelPrice, tickSize);
				foreach (var lvl in strategy.Levels)
				{
					if (lvl == null) continue;
					if (PositionMath.PriceToTicks(lvl.PriceLevel, tickSize) == key)
					{
						if (lvl.Orders != null) result.AddRange(lvl.Orders.Where(o => o != null));
						break;
					}
				}
			}
			else
			{
				const double EPS = 1e-8;
				foreach (var lvl in strategy.Levels)
				{
					if (lvl == null) continue;
					if (Math.Abs(lvl.PriceLevel - levelPrice) <= EPS)
					{
						if (lvl.Orders != null) result.AddRange(lvl.Orders.Where(o => o != null));
						break;
					}
				}
			}

			return result;
		}
	}
	
	#endregion
    // ==========================================================
    #region ChartHoverTracker

    internal sealed class ChartHoverTracker
    {
        private readonly NearestLevelResolver _nearestLevelResolver;
        private Cursor prevChartCursor;

        public float HoverX { get; private set; }
        public float HoverY { get; private set; }
        public double HoverPrice { get; private set; } = double.NaN;
        public long HoverPriceKey { get; private set; } = long.MinValue;
        public bool IsMouseInsideChartPanel { get; private set; }
        public bool IsChartControlCursorHand { get; private set; }
        public NearestLevelInfo NearestLevelInfo { get; private set; } = NearestLevelInfo.Empty;

        public ChartHoverTracker(NearestLevelResolver nearestLevelResolver)
        {
            _nearestLevelResolver = nearestLevelResolver ?? throw new ArgumentNullException(nameof(nearestLevelResolver));
        }

        public void UpdateCursorState(Cursor currentChartCursor)
        {
            if (prevChartCursor == currentChartCursor) return;

            prevChartCursor = currentChartCursor;
            IsChartControlCursorHand = currentChartCursor == Cursors.Hand;
        }

        public void OnMouseMove(float x, float y, float panelLeft, float panelRight, float panelTop, float panelBottom, 
            ChartScale chartScale, double tickSize, MergedStrategySnapshot merged, Func<double, double, double> snapToTick)
        {
            HoverX = x;
            HoverY = y;

            IsMouseInsideChartPanel = HoverX > panelLeft && HoverX < panelRight && HoverY > panelTop && HoverY < panelBottom;

            if (!IsMouseInsideChartPanel)
            {
                ClearIfAllowed(merged);
                HoverPrice = double.NaN;
                return;
            }

            if (chartScale == null) return;

            double rawPrice = chartScale.GetValueByY(HoverY);

            if (tickSize > 0.0)
            {
                double snappedPrice = snapToTick(rawPrice, tickSize);
                long key = PositionMath.PriceToTicks(snappedPrice, tickSize);

                if (key != HoverPriceKey)
                {
                    HoverPriceKey = key;
                    HoverPrice = PositionMath.TicksToPrice(key, tickSize);
                }
            }
            else
            {
                HoverPrice = snapToTick(rawPrice, tickSize);
                HoverPriceKey = long.MinValue;
            }
        }

        public void ResolveNearestLevelOnMouseDown(MergedStrategySnapshot merged,           
            double tickSize, IReadOnlyDictionary<long, PositionState> positionStates)
        {
            if (merged == null || merged.IsEmpty) return;

            var resolved = _nearestLevelResolver.Resolve(merged, HoverPrice, tickSize, positionStates);

            if (!resolved.IsFound) 
			{ 
				ClearIfAllowed(merged); 
				return; 
			}

            NearestLevelInfo = resolved;
        }

        public void ClearForced() => NearestLevelInfo = NearestLevelInfo.Empty; 

        public void ClearIfAllowed(MergedStrategySnapshot merged)
        {
            if (merged == null || merged.IsEmpty) return;
            if (IsChartControlCursorHand) return;
            ClearForced();
        }

        public void ResetInteractionState()
        {
            prevChartCursor = null;
            IsMouseInsideChartPanel = false;
            IsChartControlCursorHand = false;
        }
    }

    #endregion	
    // ==========================================================	
}
