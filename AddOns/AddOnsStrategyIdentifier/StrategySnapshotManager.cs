#region Using declarations
using System;
using System.Collections.Generic;
using System.Linq;                             
using System.Threading;                           
using System.Threading.Tasks;                    
using System.Windows.Threading;                  
using NinjaTrader.Cbi;                         
using NinjaTrader.NinjaScript;                   
using NinjaTrader.Gui.NinjaScript.AtmStrategy;  
#endregion

using NinjaTrader.NinjaScript.AddOns.AddOnsStrategyIdentifier.NtNullLogger;

//This namespace holds Add ons in this folder and is required. Do not change it. 
namespace NinjaTrader.NinjaScript.AddOns.AddOnsStrategyIdentifier.StrategySnapshotManager
{
	// ==========================================================	
    #region Strategies.Primitives
	// ==============================
    #region Enumerations
	
    [Flags]
    public enum StrategyOrderCategory { Unknown = 0, Entry = 1 << 0, Stop = 1 << 1, Target = 1 << 2 }
    public enum StrategyOrigin { UnknownStrategy = 0, ManualStrategy = 1, AtmStrategy = 2, NinjaScriptStrategy = 3 }
    public enum StrategyLifecycleState { Pending, Active, Closed }

    #endregion
	// ==============================	
    #region StrategySlotKeys

    public struct SlotKey : IEquatable<SlotKey>
    {
        public readonly string AccountName;
        public readonly string InstrumentFullName;

        public SlotKey(Account account, Instrument instrument)
            : this(account != null ? account.Name : string.Empty, instrument != null ? instrument.FullName : string.Empty) { }

        public SlotKey(string accountName, string instrumentFullName)
        {
            AccountName = accountName ?? string.Empty;
            InstrumentFullName = instrumentFullName ?? string.Empty;
        }

        public bool Equals(SlotKey other) =>
            string.Equals(AccountName, other.AccountName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(InstrumentFullName, other.InstrumentFullName, StringComparison.OrdinalIgnoreCase);

        public override bool Equals(object obj) => obj is SlotKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + StringComparer.OrdinalIgnoreCase.GetHashCode(AccountName);
                h = h * 31 + StringComparer.OrdinalIgnoreCase.GetHashCode(InstrumentFullName);
                return h;
            }
        }

        public override string ToString() => AccountName + "×" + InstrumentFullName;
    }
	
    #endregion	
	// ==============================	
    #region CompositeStrategyKey
	
    public struct CompositeStrategyKey : IEquatable<CompositeStrategyKey>
    {
        public readonly SlotKey SlotKey;
        public readonly long StrategyId;

        public CompositeStrategyKey(SlotKey slotKey, long strategyId)
        {
            SlotKey = slotKey;
            StrategyId = strategyId;
        }

        public bool Equals(CompositeStrategyKey other) => SlotKey.Equals(other.SlotKey) && StrategyId == other.StrategyId;

        public override bool Equals(object obj) => obj is CompositeStrategyKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked { return SlotKey.GetHashCode() * 31 + StrategyId.GetHashCode(); }
        }

        public override string ToString() => SlotKey.ToString() + " + " + StrategyId.ToString();           
    }

    #endregion	
	// ==============================
    #region StrategyIdentity

    public static class StrategyIdentity
    {		
		public const long UnknownStrategyId = 0L;
		public const string UnknownStrategyName = "Unknown";		
		public const long ManualStrategyId = -2L;
		public const string ManualStrategyName = "Manual";
		
        private static readonly System.Text.RegularExpressions.Regex s_trailingNumberRx =
            new System.Text.RegularExpressions.Regex(@"/(\d+)$", 
                System.Text.RegularExpressions.RegexOptions.Compiled | 
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        
        private static readonly System.Text.RegularExpressions.Regex s_idEqualsRx =
            new System.Text.RegularExpressions.Regex(@"id=(\d+)", 
                System.Text.RegularExpressions.RegexOptions.Compiled | 
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);

	    public static bool IsManualStrategy(long strategyId)
	    {
	        return strategyId == ManualStrategyId;
	    }
	
		public static long ExtractStrategyId(object owner)
		{
			if (owner == null) return ManualStrategyId;

		    try
		    {
		        if (owner is AtmStrategy atm) return atm.Id;

		        Type type = owner.GetType();

		        var idProperty = type.GetProperty("Id");
		
		        if (idProperty != null)
		        {
		            object value = idProperty.GetValue(owner);
		
		            if (value is long longId) return longId;
		            if (value is int intId) return intId;		               
		        }

		        string txt = owner.ToString() ?? string.Empty;		
		        var match = s_trailingNumberRx.Match(txt);
		
		        if (match.Success && long.TryParse(match.Groups[1].Value, out long parsed)) return parsed;

		        match = s_idEqualsRx.Match(txt);
		
		        if (match.Success && long.TryParse(match.Groups[1].Value, out parsed)) return parsed;		           

		        var nameProp = type.GetProperty("Name");		
		        string name = nameProp?.GetValue(owner)?.ToString() ?? txt;

		        return DeterministicLongHash(name + "|" + txt);
		    }
		    catch { return UnknownStrategyId; }   
		}

		private static long DeterministicLongHash(string s)
		{
			if (string.IsNullOrWhiteSpace(s)) return UnknownStrategyId;

		    unchecked
		    {
		        long h = 1469598103934665603L;
		
		        foreach (char c in s)
		        {
		            h ^= c;
		            h *= 1099511628211L;
		        }
		
		        return h & long.MaxValue;
		    }
		}
    }

    static class StrategyIdentityCache
    {
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, BoxedLong> s_cache = new(); 

        public static long GetOrExtract(object owner)
        {
            if (owner == null) return StrategyIdentity.ManualStrategyId;
            if (s_cache.TryGetValue(owner, out var boxed)) return boxed.Value;

            long id = StrategyIdentity.ExtractStrategyId(owner);
            try { s_cache.GetValue(owner, _ => new BoxedLong(id)); } catch { /* ignore */ }  

            return id;
        }
		
        private sealed class BoxedLong
        {
            public long Value { get; }
            public BoxedLong(long v) { Value = v; }
        }		
    }
			
	#endregion
	// ==============================
	#region PositionMath

	internal static class PositionMath
	{
		public static PositionSnapshot BuildAccountNetPositionSnapshotStatic(Account account, Instrument instrument)
		{
		    var positions = account.Positions?
		        .Where(p => p != null &&
		                    p.Instrument != null &&
		                    string.Equals(p.Instrument.FullName, instrument.FullName, StringComparison.OrdinalIgnoreCase))		                       
		        .ToList() ?? new List<Position>();

		    if (positions.Count == 0) return PositionSnapshot.Empty;
		
		    int netSigned = 0;
		    double longQty = 0.0, longValue = 0.0;
		    double shortQty = 0.0, shortValue = 0.0;
		
		    foreach (var p in positions)
		    {
		        if (p == null || p.MarketPosition == MarketPosition.Flat || p.Quantity == 0) continue;
				
		        double absQty = Math.Abs(p.Quantity);
		        int signed = p.MarketPosition == MarketPosition.Long ? (int)absQty : -(int)absQty;
		        netSigned += signed;
				
		        if (p.MarketPosition == MarketPosition.Long) 
				{ 
					longQty += absQty; 
					longValue += p.AveragePrice * absQty; 
				}
		        else 
				{ 
					shortQty += absQty; 
					shortValue += p.AveragePrice * absQty; 
				}
		    }
		
		    int absNet = Math.Abs(netSigned);
		    if (absNet == 0) return PositionSnapshot.Empty;
		
		    double avg = netSigned > 0 && longQty > 0 ? longValue / longQty : netSigned < 0 && shortQty > 0 ? shortValue / shortQty : 0.0;

		    return new PositionSnapshot(netSigned > 0 ? MarketPosition.Long : MarketPosition.Short, absNet, avg);     
		}		

		public static bool AccountHasGenuineOppositeSidedPositions(Account account, Instrument instrument)
		{
		    try
		    {
		        if (account?.Positions == null || instrument == null) return false;

		        bool hasLong = false; 
				bool hasShort = false;

		        foreach (var p in account.Positions)
		        {
		            if (p == null || p.Instrument == null) continue;
		            if (!string.Equals(p.Instrument.FullName, instrument.FullName, StringComparison.OrdinalIgnoreCase)) continue;
		            if (p.MarketPosition == MarketPosition.Flat || p.Quantity == 0) continue;

		            if (p.MarketPosition == MarketPosition.Long) 
						hasLong = true;
		            else if (p.MarketPosition == MarketPosition.Short) 
						hasShort = true;

		            if (hasLong && hasShort) return true;
		        }

		        return false;
		    }
		    catch { return false; }
		}

		internal static List<Order> GetLiveOrdersForInstrument(Account account, Instrument instrument)
		{
		    return account?.Orders?
		        .Where(o => o != null && 
		            o.Instrument == instrument && 
		            o.OrderState != OrderState.Initialized && 
		            o.OrderState != OrderState.CancelSubmitted && 
		            !Order.IsTerminalState(o.OrderState))
		        .ToList() ?? new List<Order>();
		}

		internal static void SnapAveragePriceToTick(PositionState state, double tickSize)
		{
		    if (tickSize > 0.0 && state.AveragePrice > 0.0)
		        state.SetAveragePrice(SnapToTickStatic(state.AveragePrice, tickSize));
		}		

		public static long PriceToTicks(double price, double tickSize)
		{
		    if (tickSize <= 0.0) throw new ArgumentException("tickSize must be > 0");

		    double snappedPrice = SnapToTickStatic(price, tickSize);
		    return (long)Math.Round(snappedPrice / tickSize, MidpointRounding.AwayFromZero);
		}
		
		public static double TicksToPrice(long ticks, double tickSize)
		{
		    if (tickSize <= 0.0) return ticks / 1_000_000.0;

		    double price = ticks * tickSize;
		    return SnapToTickStatic(price, tickSize);
		}

		internal static double SnapToTickStatic(double price, double tickSize)
		{
		    if (tickSize <= 0.0) return price;
		    double tickSizeInv = 1.0 / tickSize;
		    return Math.Round(price * tickSizeInv, MidpointRounding.AwayFromZero) * tickSize;
		}
	}

	#endregion
	// ==============================
    #endregion
	// ==========================================================
    #region Strategies.Models
	// ==============================	
	#region SlotReport

	internal sealed class SlotReport
	{
	    public SlotKey Key { get; set; }
	    public int StrategyCount { get; set; }
	    public StrategySnapshot Snapshot { get; set; }
	}

	#endregion	
	// ==============================
	#region MergedStrategySnapshot

	internal sealed class MergedStrategySnapshot
	{
	    public static readonly MergedStrategySnapshot Empty = new MergedStrategySnapshot(
	        version: 0L,
	        new Dictionary<CompositeStrategyKey, StrategyModel>(),
	        new List<SlotReport>());
	
	    public long Version { get; }
	    public IReadOnlyDictionary<CompositeStrategyKey, StrategyModel> Strategies { get; }
	    public IReadOnlyList<SlotReport> SlotReports { get; }
	
	    public MergedStrategySnapshot(long version, Dictionary<CompositeStrategyKey, StrategyModel> strategies, 
			List<SlotReport> slotReports)   
	    {
	        Version = version;
	        Strategies = strategies;
	        SlotReports = slotReports;
	    }
	
	    public bool IsEmpty => Strategies == null || Strategies.Count == 0;
	}
 
	#endregion	
	// ==============================	
    #region StrategyModel

    public sealed class StrategyModel
    {
        public long Id { get; }
        public string Name { get; }
        public IReadOnlyList<StrategyOrderDto> Entries { get; }
        public IReadOnlyList<StrategyOrderDto> Stops { get; }
        public IReadOnlyList<StrategyOrderDto> Targets { get; }
        public IReadOnlyList<StrategyPriceLevel> Levels { get; }
        public PositionSnapshot Position { get; }
        public IReadOnlyList<StrategyFillDto> Fills { get; }
	    public string AverageDisplayName { get; }
	    public bool IsAggregated { get; }	
		public IReadOnlyList<string> AggregatedStrategyNames { get; }
		public IReadOnlyList<AggregatedStrategyInfo> AggregatedStrategyInfos { get; }
		public double LevelsAveragePrice { get; }
		public DateTime FirstFillTimeUtc { get; } 

        public bool HasPosition => Position.HasPosition;
        public bool IsLong => Position.IsLong;
        public bool IsShort => Position.IsShort;
        public int QuantityPositions => Position.QuantityPositions;
        public double AveragePrice => Position.AveragePrice;

        public StrategyModel(
            long id,
            string name,
            IReadOnlyList<StrategyOrderDto> entries,
            IReadOnlyList<StrategyOrderDto> stops,
            IReadOnlyList<StrategyOrderDto> targets,
            IReadOnlyList<StrategyPriceLevel> levels,
            PositionSnapshot position,
            IReadOnlyList<StrategyFillDto> fills,
	        string averageDisplayName = null,
	        bool isAggregated = false,
			IReadOnlyList<string> aggregatedStrategyNames = null,
			IReadOnlyList<AggregatedStrategyInfo> aggregatedStrategyInfos = null,
			double levelsAveragePrice = double.NaN,
			DateTime firstFillTimeUtc = default)
        {
            Id = id;
            Name = name ?? string.Empty;
            Entries = entries ?? Array.Empty<StrategyOrderDto>();
            Stops = stops ?? Array.Empty<StrategyOrderDto>();
            Targets = targets ?? Array.Empty<StrategyOrderDto>();
            Levels = levels ?? Array.Empty<StrategyPriceLevel>();
            Position = position ?? PositionSnapshot.Empty;
            Fills = fills ?? Array.Empty<StrategyFillDto>();
	        AverageDisplayName = averageDisplayName ?? name ?? string.Empty;
	        IsAggregated = isAggregated;
			AggregatedStrategyNames = aggregatedStrategyNames ?? new List<string>();
			AggregatedStrategyInfos = aggregatedStrategyInfos ?? new List<AggregatedStrategyInfo>();
			LevelsAveragePrice = levelsAveragePrice;
			FirstFillTimeUtc = firstFillTimeUtc;
        }
    }
	
	#endregion
	// ==============================	
    #region StrategySnapshot

    public sealed class StrategySnapshot
    {
        public long Version { get; }
        public IReadOnlyDictionary<long, StrategyModel> Strategies { get; }
        public DateTime TimestampUtc { get; }

        public StrategySnapshot(long version, IReadOnlyDictionary<long, StrategyModel> strategies) 
        {
            Version = version;
            Strategies = strategies ?? new Dictionary<long, StrategyModel>();
            TimestampUtc = DateTime.UtcNow;
        }

        public static readonly StrategySnapshot Empty = new StrategySnapshot(0, new Dictionary<long, StrategyModel>()); 
           
    }

	#endregion	
	// ==============================
    #region PositionState

    public sealed class PositionState
    {
		private readonly HashSet<string> _appliedOrderIds = new HashSet<string>(StringComparer.Ordinal);
        private long _signedQty = 0;		

        public long Id { get; }
        public StrategyLifecycleState LifecycleState { get; private set; }
        public MarketPosition Direction { get; private set; }
        public int Quantity { get; private set; }
        public double AveragePrice { get; private set; }
        public DateTime FirstFillTimeUtc { get; private set; }
        public DateTime LastExecutionTimeUtc { get; private set; }
        public DateTime LastActivityUtc { get; private set; }
        public bool IsInitialized { get; private set; }
		public StrategyOrigin Origin { get; private set; } = StrategyOrigin.UnknownStrategy;
		public string KnownName { get; private set; } = string.Empty;

		public void SetOrigin(StrategyOrigin origin)
		{
		    if (GetOriginPriority(origin) > GetOriginPriority(Origin))		       
		        Origin = origin;
		}
	
		private static int GetOriginPriority(StrategyOrigin origin)
		{
		    switch (origin)
		    {
		        case StrategyOrigin.UnknownStrategy: return 0;
		        case StrategyOrigin.ManualStrategy: return 1;		           		
		        case StrategyOrigin.AtmStrategy: return 2;		           		
		        case StrategyOrigin.NinjaScriptStrategy: return 3;		           		
		        default: return 0;		           
		    }
		}

		public bool SetKnownName(string name)
		{
		    if (string.IsNullOrWhiteSpace(name)) return true;
		
		    if (string.IsNullOrWhiteSpace(KnownName))
		    {
		        KnownName = name;
		        return true;
		    }
		
		    return string.Equals(KnownName, name, StringComparison.Ordinal);
		}		

        public void SetAveragePrice(double snappedPrice)
        {
            if (snappedPrice > 0.0)
                AveragePrice = snappedPrice;
        }

		internal void CorrectQuantity(int newQty)
		{
		    if (newQty <= 0) return;
		    Quantity = newQty;
		    DeriveLifecycle();
		}		

        public void ApplyExecution(OrderAction orderAction, 
			int execQty, double execPrice, DateTime execTimeUtc, double pointValue, string orderId = "")	
        {
            if (execQty <= 0) return;
			if (!string.IsNullOrEmpty(orderId) && !_appliedOrderIds.Add(orderId)) return;
			
            if (pointValue < 0) pointValue = 0.0;

            LastActivityUtc = DateTime.UtcNow;

            long deltaSigned = orderAction switch
            {
                OrderAction.Buy => execQty,
                OrderAction.BuyToCover => execQty,
                OrderAction.Sell => -execQty,
                OrderAction.SellShort => -execQty,
                _ => execQty
            };

            if (!IsInitialized)
            {
                FirstFillTimeUtc = execTimeUtc;
                IsInitialized = true;
            }
            LastExecutionTimeUtc = execTimeUtc;

            long prevSigned = _signedQty;
            long nextSigned = prevSigned + deltaSigned;
            bool sameDirection = (prevSigned > 0 && deltaSigned > 0) || (prevSigned < 0 && deltaSigned < 0); 

            if (prevSigned == 0)
            {
                _signedQty = nextSigned;
                Quantity = (int)Math.Abs(_signedQty);
                Direction = _signedQty > 0 ? MarketPosition.Long : _signedQty < 0 ? MarketPosition.Short : MarketPosition.Flat; 
                AveragePrice = execPrice;
            }
            else if (sameDirection)
            {
                int prevAbs = (int)Math.Abs(prevSigned);
                int execAbs = (int)Math.Abs(deltaSigned);
                double prevAvg = AveragePrice;
                double newAvg = 0.0;
                if (prevAbs + execAbs > 0)
                    newAvg = (prevAvg * prevAbs + execPrice * execAbs) / (prevAbs + execAbs);
                AveragePrice = newAvg;
                _signedQty = nextSigned;
                Quantity = (int)Math.Abs(_signedQty);
                Direction = _signedQty > 0 ? MarketPosition.Long : MarketPosition.Short;
            }
            else
            {
                int prevAbs = (int)Math.Abs(prevSigned);
                int deltaAbs = (int)Math.Abs(deltaSigned);

                if (deltaAbs < prevAbs)
                {
                    int closedQty = deltaAbs;
                    _signedQty = nextSigned;
                    Quantity = (int)Math.Abs(_signedQty);
                    Direction = _signedQty > 0 ? MarketPosition.Long : MarketPosition.Short;
                }
                else if (deltaAbs == prevAbs)
                {
                    int closedQty = deltaAbs;
                    _signedQty = 0;
                    Quantity = 0;
                    AveragePrice = 0.0;
                    Direction = MarketPosition.Flat;
                    LifecycleState = StrategyLifecycleState.Closed;
                }
                else
                {
                    int closedQty = prevAbs;
                    int openedQty = deltaAbs - prevAbs;
                    _signedQty = nextSigned;
                    Quantity = openedQty;
                    AveragePrice = execPrice;
                    Direction = _signedQty > 0 ? MarketPosition.Long : MarketPosition.Short;
                    LifecycleState = StrategyLifecycleState.Active;
                }
            }

            if (Quantity > 0)
                LifecycleState = StrategyLifecycleState.Active;
            else
                LifecycleState = StrategyLifecycleState.Closed;
        }

        public void RestoreFromCache(long signedQtyValue, int quantity, double averagePrice, 
			MarketPosition direction, DateTime firstFillTimeUtcValue, DateTime lastExecutionTimeUtcValue)   
        {
            _signedQty = signedQtyValue;
            Quantity = quantity;
            AveragePrice = averagePrice;
            Direction = direction;
            FirstFillTimeUtc = firstFillTimeUtcValue;
            LastExecutionTimeUtc = lastExecutionTimeUtcValue;
            LastActivityUtc = DateTime.UtcNow;
            IsInitialized = quantity > 0;

            DeriveLifecycle();
        }

        private void DeriveLifecycle()
        {
            if (Quantity <= 0)
            {
                LifecycleState = StrategyLifecycleState.Closed;
                return;
            }

            if (Quantity > 0 && IsInitialized)
            {
                LifecycleState = StrategyLifecycleState.Active;
                return;
            }

            LifecycleState = StrategyLifecycleState.Pending;
        }

        public long GetSignedQtyForCaching() => _signedQty;

        public bool IsHealthyAfterRestore()
        {
            bool consistentQuantity = (Quantity > 0) == (Direction != MarketPosition.Flat);

            bool consistentLifecycle =
                (LifecycleState == StrategyLifecycleState.Closed && Quantity == 0) ||
                (LifecycleState == StrategyLifecycleState.Active && Quantity > 0) ||
                (LifecycleState == StrategyLifecycleState.Pending && !IsInitialized);

            return consistentQuantity && consistentLifecycle;
        }

        public PositionState(long id)
        {
            Id = id;
            LifecycleState = StrategyLifecycleState.Pending;
            Direction = MarketPosition.Flat;
            Quantity = 0;
            AveragePrice = 0.0;
            FirstFillTimeUtc = DateTime.MinValue;
            LastExecutionTimeUtc = DateTime.MinValue;
            LastActivityUtc = DateTime.UtcNow;
            IsInitialized = false;
            _signedQty = 0;
        }
    }

    #endregion	
	// ==============================	
    #region PositionSnapshot

    public sealed class PositionSnapshot
    {
        public MarketPosition MarketPosition { get; }
        public int QuantityPositions { get; }
        public double AveragePrice { get; }
        
        public bool IsLong => MarketPosition == MarketPosition.Long;
        public bool IsShort => MarketPosition == MarketPosition.Short;
        public bool HasPosition => MarketPosition != MarketPosition.Flat;

        public PositionSnapshot(MarketPosition marketPosition, int quantityPositions, double averagePrice)
        {
            MarketPosition = marketPosition;
            QuantityPositions = quantityPositions;
            AveragePrice = averagePrice;
        }

        public static readonly PositionSnapshot Empty = new PositionSnapshot(MarketPosition.Flat, 0, 0.0);
    }

	#endregion	
	// ==============================
    #region StrategyOrderDto

	public sealed class StrategyOrderDto
	{
	    public long StrategyId { get; }
	    public string OrderId { get; }
	    public string OrderName { get; }
	    public int QuantityOrders { get; }
	    public double LimitPrice { get; }
	    public double StopPrice { get; }
	    public OrderState State { get; }
	    public StrategyOrderCategory Category { get; }
	    public string RawOwnerInfo { get; }
	    public StrategyOrigin Origin { get; }
	    public OrderType OrderType { get; }
	    public OrderAction Action { get; }
	    public DateTime CreatedOrderTime { get; }
	
	    public StrategyOrderDto(long strategyId, string orderId, string orderName, int quantityOrders, double limitPrice, double stopPrice, OrderState state,  
			StrategyOrderCategory category, string rawOwnerInfo, StrategyOrigin origin, OrderType orderType, OrderAction action, DateTime createdOrderTime)
	    {
	        StrategyId = strategyId;
	        OrderId = orderId ?? string.Empty;
	        OrderName = orderName ?? string.Empty;
	        QuantityOrders = quantityOrders;
	        LimitPrice = limitPrice;
	        StopPrice = stopPrice;
	        State = state;
	        Category = category;
	        RawOwnerInfo = rawOwnerInfo ?? string.Empty;
	        Origin = origin;
	        OrderType = orderType;
	        Action = action;
	        CreatedOrderTime = createdOrderTime;
	    }
	}

	#endregion	
	// ==============================	
    #region StrategyFillDto
	
    public sealed class StrategyFillDto
    {
        public long Sequence { get; }
        public DateTime TimeUtc { get; }
        public OrderAction Action { get; }
        public int QuantityOrders { get; }
        public double Price { get; }
        public string OrderId { get; }
        public string RawOwnerInfo { get; }

        public StrategyFillDto(long sequence, DateTime timeUtc, OrderAction action,
            int quantityOrders, double price, string orderId = "", string rawOwnerInfo = "")
        {
            Sequence = sequence;
            TimeUtc = timeUtc;
            Action = action;
            QuantityOrders = quantityOrders;
            Price = price;
            OrderId = orderId ?? string.Empty;
            RawOwnerInfo = rawOwnerInfo ?? string.Empty;
        }
    }

	#endregion
	// ==============================	
    #region AggregatedStrategyInfo
	
	public sealed class AggregatedStrategyInfo
	{
	    public long Id { get; }
	    public string Name { get; }
	    public MarketPosition Direction { get; }
	    public DateTime FirstFillTimeUtc { get; }

	    public AggregatedStrategyInfo(long id, string name, MarketPosition direction, DateTime firstFillTimeUtc)
	    {
	        Id = id;
	        Name = name ?? string.Empty;
	        Direction = direction;
	        FirstFillTimeUtc = firstFillTimeUtc;
	    }
	}		

	#endregion		
	// ==============================
    #region StrategyOrderBook
	
    public sealed class StrategyOrderBook
    {
        public long Id { get; }
        public string Name { get; }
        public IReadOnlyList<StrategyOrderDto> Entries { get; }
        public IReadOnlyList<StrategyOrderDto> Stops { get; }
        public IReadOnlyList<StrategyOrderDto> Targets { get; }

        public StrategyOrderBook(long id, string name,
            IReadOnlyList<StrategyOrderDto> entries, IReadOnlyList<StrategyOrderDto> stops, IReadOnlyList<StrategyOrderDto> targets)
        {
            Id = id;
            Name = name ?? string.Empty;
            Entries = entries ?? Array.Empty<StrategyOrderDto>();
            Stops = stops ?? Array.Empty<StrategyOrderDto>();
            Targets = targets ?? Array.Empty<StrategyOrderDto>();
        }
    }

	#endregion	
	// ==============================
    #region StrategyPriceLevel

    public sealed class StrategyPriceLevel
    {
        public double PriceLevel { get; }
        public double Amount { get; }
        public int QuantityOrders { get; }
        public StrategyOrderCategory Kind { get; }
        public IReadOnlyList<StrategyOrderDto> Orders { get; }

        public bool HasStops => (Kind & StrategyOrderCategory.Stop) != 0;
        public bool HasTargets => (Kind & StrategyOrderCategory.Target) != 0;
		public bool HasEntries => (Kind & StrategyOrderCategory.Entry) != 0;

        public StrategyPriceLevel(double priceLevel, double amount,
            int quantityOrders, StrategyOrderCategory kind, IReadOnlyList<StrategyOrderDto> orders)
        {
            PriceLevel = priceLevel;
            Amount = amount;
            QuantityOrders = quantityOrders;
            Kind = kind;
            Orders = orders ?? Array.Empty<StrategyOrderDto>();
        }
    }
			
	#endregion	
	// ==============================
    #endregion
	// ==========================================================
    #region Strategies.Storage
	// ==============================	
    #region StrategyExecutionStore

    public sealed class StrategyExecutionStore : IDisposable
    {
        private const int MaxExecutions = 5000;

        private readonly IStrategyLogger _logger;

        private readonly object _masterLock = new();
        private readonly LinkedList<ExecutionRecord> _masterExecutions = new();

        private long _executionSequence = 0;

        public StrategyExecutionStore(IStrategyLogger logger)
        {
            _logger = logger ?? NullLogger.Instance;
        }

        public void RecordExecution(string accountName, string instrumentFullName, 
			OrderAction action, int quantity, double price, string orderId, string ownerInfo, bool isManual)
        {
            try
            {
                if (quantity <= 0) return;

                var seq = Interlocked.Increment(ref _executionSequence);
                var exec = new ExecutionRecord(
                    seq,
                    DateTime.UtcNow,
                    accountName,
                    instrumentFullName,
                    action,
                    Math.Abs(quantity),
                    price,
                    orderId,
                    ownerInfo,
                    isManual);

                lock (_masterLock)
                {
                    _masterExecutions.AddLast(exec);

                    if (_masterExecutions.Count > MaxExecutions)
                    {
                        _masterExecutions.RemoveFirst();
                        _logger.Debug($"ExecutionStore.RecordExecution Cleanup: removed oldest execution");
                    }
                }

                _logger.Debug($"ExecutionStore.RecordExecution => {exec}");
            }
            catch (Exception ex) { _logger.Error($"ExecutionStore.RecordExecution error: {ex.Message}"); }
        }

        public void Dispose() { }

        public sealed class ExecutionRecord
        {
            public long Sequence { get; }
            public DateTime TimeUtc { get; }
            public string AccountName { get; }
            public string InstrumentFullName { get; }
            public OrderAction Action { get; }
            public int Quantity { get; }
            public double Price { get; }
            public string OrderId { get; }
            public string OwnerInfo { get; }
            public bool IsManual { get; }

            public ExecutionRecord(long sequence, DateTime timeUtc, string accountName, string instrumentFullName, 
				OrderAction action, int quantity, double price, string orderId, string ownerInfo, bool isManual)   
            {
                Sequence = sequence;
                TimeUtc = timeUtc;
                AccountName = accountName ?? string.Empty;
                InstrumentFullName = instrumentFullName ?? string.Empty;
                Action = action;
                Quantity = quantity;
                Price = price;
                OrderId = orderId ?? string.Empty;
                OwnerInfo = ownerInfo ?? string.Empty;
                IsManual = isManual;
            }

            public override string ToString() =>
                $"Sequence={Sequence} | Account={AccountName} | Instrument={InstrumentFullName} | " +
                $"Action={Action} | Qty={Quantity} | Price={Price} | Manual={IsManual}";
        }
    }

    #endregion
	// ==============================	
    #region StrategyFillsStore

    internal sealed class StrategyFillsStore
    {
        private const int MaxFillsStored = 100;
        private const int MaxTrackedStrategies = 300;

        private sealed class FillsEntry
        {
            public readonly LinkedList<StrategyFillDto> Fills = new LinkedList<StrategyFillDto>();
            public long FillSequence;
        }

        private readonly object _lock = new object();
        private readonly BoundedFifoCache<long, FillsEntry> _byStrategy = new BoundedFifoCache<long, FillsEntry>(MaxTrackedStrategies);
        private readonly IStrategyLogger _logger;

        public StrategyFillsStore(IStrategyLogger logger)
        {
            _logger = logger ?? NullLogger.Instance;
        }

        public StrategyFillDto RecordFill(long strategyId, OrderAction action, 
			int quantity, double price, DateTime timeUtc, string orderId = "", string rawOwnerInfo = "")
        {
            if (quantity <= 0) return null;

            try
            {
                lock (_lock)
                {
                    if (!_byStrategy.TryGet(strategyId, out var entry))
                        entry = new FillsEntry();

                    var seq = Interlocked.Increment(ref entry.FillSequence);
                    var fe = new StrategyFillDto(seq, timeUtc, action, quantity, price, orderId ?? string.Empty, rawOwnerInfo ?? string.Empty);
                    entry.Fills.AddLast(fe);
                    if (entry.Fills.Count > MaxFillsStored)
                        entry.Fills.RemoveFirst();

                    _byStrategy.Set(strategyId, entry);

                    return fe;
                }
            }
            catch (Exception ex) { _logger.Error($"StrategyFillsStore.RecordFill error: {ex.Message}"); return null; }
        }

        public IReadOnlyList<StrategyFillDto> GetFills(long strategyId)
        {
            try
            {
                lock (_lock)
                {
                    return _byStrategy.TryGet(strategyId, out var entry)
                        ? entry.Fills.ToList().AsReadOnly()
                        : (IReadOnlyList<StrategyFillDto>)Array.Empty<StrategyFillDto>();
                }
            }
            catch (Exception ex) { _logger.Error($"StrategyFillsStore.GetFills error: {ex.Message}"); return Array.Empty<StrategyFillDto>(); }
        }

        public void RestoreFills(long strategyId, IEnumerable<StrategyFillDto> fills)
        {
            try
            {
                lock (_lock)
                {
                    var entry = new FillsEntry();
                    if (fills != null)
                        foreach (var f in fills)
                            if (f != null) 
								entry.Fills.AddLast(f);

                    if (entry.Fills.Count > 0)
                        entry.FillSequence = entry.Fills.Last.Value.Sequence;

                    _byStrategy.Set(strategyId, entry);
                }
            }
            catch (Exception ex) { _logger.Error($"StrategyFillsStore.RestoreFills error: {ex.Message}"); }
        }

        public void Clear()
        {
            try { lock (_lock) { _byStrategy.Clear(); } }
            catch (Exception ex) { _logger.Error($"StrategyFillsStore.Clear error: {ex.Message}"); }
        }
    }

    #endregion
	// ==============================	
    #region StrategyOrderBookStore

    internal sealed class StrategyOrderBookStore
    {
        private volatile Dictionary<long, StrategyOrderBook> _orders = new Dictionary<long, StrategyOrderBook>();

        #region Public API

        public IReadOnlyDictionary<long, StrategyOrderBook> GetAll() => _orders;

        public bool TryGet(long strategyId, out StrategyOrderBook orders) => _orders.TryGetValue(strategyId, out orders);

        public void Upsert(long strategyId, StrategyOrderBook orders)
        {
            var updated = new Dictionary<long, StrategyOrderBook>(_orders) { [strategyId] = orders };
            _orders = updated;
        }

        public void Remove(long strategyId)
        {
            var current = _orders;
            if (!current.ContainsKey(strategyId)) return;

            var updated = new Dictionary<long, StrategyOrderBook>(current);
            updated.Remove(strategyId);
            _orders = updated;
        }

        public void ReplaceAll(Dictionary<long, StrategyOrderBook> newOrders)
        {
            _orders = new Dictionary<long, StrategyOrderBook>(newOrders ?? new Dictionary<long, StrategyOrderBook>());
        }

        public void Clear()
        {
            _orders = new Dictionary<long, StrategyOrderBook>();
        }

        #endregion
    }

    #endregion	
	// ==============================	
    #region StrategyPositionStore

    internal sealed class StrategyPositionStore
    {
        private readonly IStrategyLogger _logger;
        private readonly StrategyCacheManager _cacheManager;
        private readonly PositionStateRestorer _restorer;
        private readonly StrategyFillsStore _fillsStore;

        private volatile Dictionary<long, PositionState> _states = new Dictionary<long, PositionState>();
        // ──────────────────────────────
        #region Constructor

        public StrategyPositionStore(SlotKey slot, IStrategyLogger logger, StrategyFillsStore fillsStore)
        {
            _logger = logger ?? NullLogger.Instance;
            _cacheManager = new StrategyCacheManager(slot, _logger);
            _fillsStore = fillsStore;
            _restorer = new PositionStateRestorer(_cacheManager, _fillsStore, _logger);
        }

        #endregion
        // ──────────────────────────────
        #region Public API

        public IReadOnlyDictionary<long, PositionState> GetStates() => _states;

        public IReadOnlyDictionary<long, PositionState> GetSnapshot()
        {
            return new Dictionary<long, PositionState>(_states);
        }

        public bool TryGetState(long strategyId, out PositionState state)
        {
            return _states.TryGetValue(strategyId, out state);
        }

		public void SaveState(long strategyId, Account account, Instrument instrument, PositionState state)
		{
		    try
		    {
		        if (state == null || account == null || instrument == null) return;
		
		        var updated = new Dictionary<long, PositionState>(_states);
		        updated[strategyId] = state;
		        _states = updated;
		
		        _cacheManager.SaveToMemCache(ToCachedPositionState(strategyId, state, _fillsStore));
		    }
		    catch (Exception ex) { _logger.Error($"StrategyPositionStore.SaveState error: {ex.Message}"); }
		}

		public void RemoveState(long strategyId, Account account, Instrument instrument)
		{
		    try
		    {
		        if (account == null || instrument == null) return;

		        var updated = new Dictionary<long, PositionState>(_states);
		        updated.Remove(strategyId);
		        _states = updated;
		    }
		    catch (Exception ex) { _logger.Error($"StrategyPositionStore.RemoveState error: {ex.Message}"); }
		}

		private static CachedPositionState ToCachedPositionState(long strategyId, PositionState state, StrategyFillsStore fillsStore)
		{
		    return new CachedPositionState
		    {
		        StrategyIdString = strategyId.ToString(),
		        Direction = state.Direction,
		        Quantity = state.Quantity,
		        AveragePrice = state.AveragePrice,
		        SignedQty = state.GetSignedQtyForCaching(),
		        FirstFillTimeUtc = state.FirstFillTimeUtc,
		        LastExecutionTimeUtc = state.LastExecutionTimeUtc,
		        Fills = fillsStore?.GetFills(strategyId)?.ToList() ?? new List<StrategyFillDto>(),
		        Origin = state.Origin,
		        CachedAt = DateTime.UtcNow
		    };
		}

        public void ClearStates()
        {
            _states = new Dictionary<long, PositionState>();
        }

        #endregion
		// ──────────────────────────────
        #region Restore from Cache (public)

        public bool TryRestore(Account account, Instrument instrument, ICollection<long> idsPendingCleanup = null)
        {
            try
            {
                if (account == null || instrument == null) return false;
                if (_states.Count > 0) return true; 

                var toRestore = _cacheManager.LoadFromMemCache();
                if (toRestore.Count == 0) return false;

                if (!_restorer.TryApply(_states, toRestore, account, instrument, idsPendingCleanup, out var restoredStates)) return false;

                _states = restoredStates;
                return true;
            }
            catch (Exception ex) { _logger.Error($"StrategyPositionStore.TryRestore error: {ex.Message}"); return false; }
        }

        #endregion
        // ──────────────────────────────
    }	
	
    #endregion	
	// ==============================
    #region PositionStateRestorer

    internal sealed class PositionStateRestorer
    {
        private readonly StrategyCacheManager _cacheManager;
        private readonly StrategyFillsStore _fillsStore;
        private readonly IStrategyLogger _logger;

        public PositionStateRestorer(StrategyCacheManager cacheManager, StrategyFillsStore fillsStore, IStrategyLogger logger)
        {
            _cacheManager = cacheManager;
            _fillsStore = fillsStore;
            _logger = logger ?? NullLogger.Instance;
        }

        public bool TryApply(Dictionary<long, PositionState> currentStates, List<CachedPositionState> entries, 
			Account account, Instrument instrument, ICollection<long> idsPendingCleanup, out Dictionary<long, PositionState> newStates)
        {
			int withPosition = entries.Count(e => e.Quantity > 0);
			int totalRestoredQty = entries.Where(e => e.Quantity > 0).Sum(e => e.Quantity);

            newStates = new Dictionary<long, PositionState>(currentStates);

            foreach (var entry in entries)
            {
                try
                {
                    long typedId;
                    if (!long.TryParse(entry.StrategyIdString, out typedId)) continue;
                    if (newStates.ContainsKey(typedId)) continue;
					
					if (idsPendingCleanup != null && idsPendingCleanup.Contains(typedId))
					{
					    _logger.Debug(
					        $"PositionStateRestorer.TryApply: " +
					        $"Id={typedId} pending cleanup - restore skipped");
						
					    continue;
					}					

                    var restored = new PositionState(typedId);
                    restored.RestoreFromCache(
                        entry.SignedQty,
                        entry.Quantity,
                        entry.AveragePrice,
                        entry.Direction,
                        entry.FirstFillTimeUtc,
                        entry.LastExecutionTimeUtc);
                    restored.SetOrigin(entry.Origin);
                    _fillsStore?.RestoreFills(typedId, entry.Fills ?? new List<StrategyFillDto>());

                    if (!restored.IsHealthyAfterRestore())
                    {
                        _logger.Debug(
                            $"PositionStateRestorer.TryApply: " +
                            $"Id={typedId} unhealthy after restore " +
                            $"(Qty={restored.Quantity} | Lifecycle={restored.LifecycleState}) - skipped");
						
                        continue;
                    }

                    if (restored.Quantity == 0 && restored.LifecycleState == StrategyLifecycleState.Closed)                       
                    {
                        _logger.Debug(
                            $"PositionStateRestorer.TryApply: " +
                            $"Id={typedId} removing from cache (Closed+Qty=0)");
						
                        _cacheManager.RemoveFromMemCache(typedId.ToString());
                        continue;
                    }

                    if (!ValidateAndCorrect(restored, account, instrument, withPosition, totalRestoredQty)) continue;

                    newStates[typedId] = restored;

                    _logger.Debug(
                        $"PositionStateRestorer.TryApply: " +
                        $"restored Id={typedId} " +
                        $"(Qty={entry.Quantity} | Avg={restored.AveragePrice:F6} | " +
                        $"Lifecycle={restored.LifecycleState})");					
                }
                catch (Exception ex) { _logger.Error( $"PositionStateRestorer.TryApply entry error: {ex.Message}"); } 
            }

            var accountPos = BuildAccountNetPositionSnapshot(account, instrument);
            int totalFinalQty = newStates.Values.Sum(s => s.Quantity);

            if (accountPos.HasPosition && totalFinalQty != accountPos.QuantityPositions)
            {
                _logger.Debug(
                    $"PositionStateRestorer.TryApply: " +
                    $"post-restore total qty={totalFinalQty} != account qty={accountPos.QuantityPositions} " +
                    $"- restore not trustworthy, caller must fall through to full reconcile");
                
                newStates = null;
                return false;
            }

            if (!accountPos.HasPosition && totalFinalQty != 0)
            {
                _logger.Debug(
                    $"PositionStateRestorer.TryApply: " +
                    $"account is flat but restored total qty={totalFinalQty} - restore not trustworthy, caller must fall through to full reconcile");

                newStates = null;
                return false;
            }

            return true;
        }

        private bool ValidateAndCorrect(PositionState restored, Account account, Instrument instrument, int totalRestoredCount, int totalRestoredQty)
        {
		    if (restored.Quantity == 0 || restored.Direction == MarketPosition.Flat) return true;
       						
            var accountPos = BuildAccountNetPositionSnapshot(account, instrument);

            if (!accountPos.HasPosition)
            {
                _logger.Debug(
                    $"PositionStateRestorer.ValidateAndCorrect: " +
                    $"Id={restored.Id} - account is flat, discarding");
				
                return false;
            }

            if (restored.Quantity > 0 && restored.Direction != accountPos.MarketPosition)               
            {
                _logger.Debug(
                    $"PositionStateRestorer.ValidateAndCorrect: " +
                    $"Id={restored.Id} direction mismatch (cache={restored.Direction} account={accountPos.MarketPosition}) - discarding");

                return false;
            }

            if (totalRestoredCount == 1 && restored.Quantity != accountPos.QuantityPositions)               
            {
                _logger.Debug(
                    $"PositionStateRestorer.ValidateAndCorrect: " +
                    $"Id={restored.Id} single-strategy qty mismatch " +
                    $"(cache={restored.Quantity} account={accountPos.QuantityPositions}) - correcting");
				
                restored.CorrectQuantity(accountPos.QuantityPositions);
            }

            if (totalRestoredCount > 1 && totalRestoredQty > accountPos.QuantityPositions)
            {
                _logger.Debug(
                    $"PositionStateRestorer.ValidateAndCorrect: " +
                    $"Id={restored.Id} multi-strategy total qty={totalRestoredQty} exceeds account qty={accountPos.QuantityPositions} - discarding");

                return false;
            }

            if (totalRestoredCount == 1 && restored.Quantity > 0)
            {
                double delta = Math.Abs(restored.AveragePrice - accountPos.AveragePrice);
                double oneTick = GetTickSizeForInstrument(instrument);
                if (delta > oneTick)
                {
                    _logger.Debug(
                        $"PositionStateRestorer.ValidateAndCorrect: " +
                        $"Id={restored.Id} AVG mismatch (cache={restored.AveragePrice:F6} account={accountPos.AveragePrice:F6}) - correcting");

                    restored.SetAveragePrice(accountPos.AveragePrice);
                }
            }
            else if (totalRestoredCount > 1)
            {
                double delta = Math.Abs(restored.AveragePrice - accountPos.AveragePrice);
                if (delta > 0)
                    _logger.Debug(
                        $"PositionStateRestorer.ValidateAndCorrect: " +
                        $"Id={restored.Id} multi-strategy AVG delta={delta:F6} " +
                        $"(cache={restored.AveragePrice:F6} vs account={accountPos.AveragePrice:F6})");					
            }

            return true;
        }

        private PositionSnapshot BuildAccountNetPositionSnapshot(Account account, Instrument instrument)
        {
            return PositionMath.BuildAccountNetPositionSnapshotStatic(account, instrument);
        }

        private double GetTickSizeForInstrument(Instrument instrument)
        {
            return instrument?.MasterInstrument?.TickSize ?? 0.25;
        }
    }

    #endregion	
	// ==============================
    #region StrategyCacheManager

    internal sealed class StrategyCacheManager
    {
        public const int MaxCachedStrategyStates = 500;

        private static readonly BoundedFifoCache<(SlotKey Slot, string StrategyIdString), CachedPositionState> s_statesCache =
            new BoundedFifoCache<(SlotKey Slot, string StrategyIdString), CachedPositionState>(MaxCachedStrategyStates);

        private readonly SlotKey _slot;
        private readonly IStrategyLogger _logger;

        public StrategyCacheManager(SlotKey slot, IStrategyLogger logger)
        {
            _slot = slot;
            _logger = logger ?? NullLogger.Instance;
        }
        // ──────────────────────────────
        #region Mem cache

        public List<CachedPositionState> LoadFromMemCache()
        {
            var result = new List<CachedPositionState>();
            foreach (var kv in s_statesCache.GetAll())
                if (kv.Key.Slot.Equals(_slot))
                    result.Add(kv.Value);
            return result;
        }

        public void SaveToMemCache(CachedPositionState entry)
        {
            try
            {
                entry.CachedAt = DateTime.UtcNow;
                s_statesCache.Set((_slot, entry.StrategyIdString), entry);

                _logger.Debug(
                    $"StrategyCacheManager.SaveToMemCache: " +
                    $"[{_slot}] Strategy={entry.StrategyIdString} | Qty={entry.Quantity} | " +
                    $"Avg={entry.AveragePrice:F6} | Fills={entry.Fills?.Count ?? 0}");               
            }
            catch (Exception ex) { _logger.Error($"StrategyCacheManager.SaveToMemCache error: {ex.Message}"); }
        }

        public void RemoveFromMemCache(string strategyIdString)
        {
            try { s_statesCache.Remove((_slot, strategyIdString)); }
            catch (Exception ex) { _logger.Error($"StrategyCacheManager.RemoveFromMemCache error: {ex.Message}"); }
        }

        public static void ClearAll()
        {
            try { s_statesCache.Clear(); }
            catch (Exception ex) { NinjaTrader.Code.Output.Process("StrategyCacheManager.ClearAll error: " + ex.Message, PrintTo.OutputTab1); }  
        }

        #endregion
        // ──────────────────────────────
    }

    #endregion	
	// ==============================
    #region CachedPositionState

    internal sealed class CachedPositionState
    {
        public string StrategyIdString;
        public MarketPosition Direction;
        public int Quantity;
        public double AveragePrice;
        public long SignedQty;
        public DateTime FirstFillTimeUtc;
        public DateTime LastExecutionTimeUtc;
        public List<StrategyFillDto> Fills;
        public StrategyOrigin Origin;
        public DateTime CachedAt;
    }

    #endregion		
	// ==============================
    #endregion
	// ==========================================================
    #region Strategies.Access
	// ==============================		
    #region Interfaces
	
    internal interface IStrategyPositionsAccess
    {
        bool TryGet(long strategyId, out PositionState state);
        void Save(long strategyId, PositionState state);
    }

    internal interface IStrategyOrdersAccess
    {
        bool TryGet(long strategyId, out StrategyOrderBook orders);
        void Save(long strategyId, StrategyOrderBook orders);
        void Remove(long strategyId);
    }

    internal interface IStrategyFillsAccess
    {
        IReadOnlyList<StrategyFillDto> Get(long strategyId);
        StrategyFillDto Record(long strategyId, OrderAction action,  
            int quantity, double price, DateTime timeUtc, string orderId, string rawOwnerInfo);
    }

    #endregion
	// ==============================		
    #region Scoped Access	
	
    internal sealed class ScopedPositionsAccess : IStrategyPositionsAccess
    {
        private readonly StrategyPositionStore _store;
        private readonly Account _account;
        private readonly Instrument _instrument;
        private readonly HashSet<long> _allowedIds;
        private readonly IStrategyLogger _logger;

        public ScopedPositionsAccess(StrategyPositionStore store, 
			Account account, Instrument instrument, HashSet<long> allowedIds, IStrategyLogger logger)
        {
            _store = store;
            _account = account;
            _instrument = instrument;
            _allowedIds = allowedIds;
            _logger = logger ?? NullLogger.Instance;
        }

        public bool TryGet(long strategyId, out PositionState state)
        {
            if (!_allowedIds.Contains(strategyId))
            {
                state = null;
                return false;
            }
            return _store.TryGetState(strategyId, out state);
        }

        public void Save(long strategyId, PositionState state)
        {
            if (!_allowedIds.Contains(strategyId))
            {
                _logger.Debug(
                    $"ScopedPositionsAccess.Save: " +
                    $"attempted to write outside allowed scope, StrategyId={strategyId} - ignored. This indicates a handler isolation bug.");
				
                return;
            }
            _store.SaveState(strategyId, _account, _instrument, state);
        }
    }

    internal sealed class ScopedOrdersAccess : IStrategyOrdersAccess
    {
        private readonly StrategyOrderBookStore _store;
        private readonly HashSet<long> _allowedIds;
        private readonly IStrategyLogger _logger;

        public ScopedOrdersAccess(StrategyOrderBookStore store, HashSet<long> allowedIds, IStrategyLogger logger) 
        {
            _store = store;
            _allowedIds = allowedIds;
            _logger = logger ?? NullLogger.Instance;
        }

        public bool TryGet(long strategyId, out StrategyOrderBook orders)
        {
            if (!_allowedIds.Contains(strategyId))
            {
                orders = null;
                return false;
            }
            return _store.TryGet(strategyId, out orders);
        }

        public void Save(long strategyId, StrategyOrderBook orders)
        {
            if (!_allowedIds.Contains(strategyId))
            {
                _logger.Debug(
                    $"ScopedOrdersAccess.Save: " +
                    $"attempted to write outside allowed scope, StrategyId={strategyId} - ignored. This indicates a handler isolation bug.");
				
                return;
            }
            _store.Upsert(strategyId, orders);
        }

        public void Remove(long strategyId)
        {
            if (!_allowedIds.Contains(strategyId))
            {
                _logger.Debug(
                    $"ScopedOrdersAccess.Remove: " +
                    $"attempted to remove outside allowed scope, StrategyId={strategyId} - ignored. This indicates a handler isolation bug.");
				
                return;
            }
            _store.Remove(strategyId);
        }
    }	

    internal sealed class ScopedFillsAccess : IStrategyFillsAccess
    {
        private readonly StrategyFillsStore _store;
        private readonly HashSet<long> _allowedIds;
        private readonly IStrategyLogger _logger;

        public ScopedFillsAccess(StrategyFillsStore store, HashSet<long> allowedIds, IStrategyLogger logger)
        {
            _store = store;
            _allowedIds = allowedIds;
            _logger = logger ?? NullLogger.Instance;
        }

        public IReadOnlyList<StrategyFillDto> Get(long strategyId)
        {
            if (!_allowedIds.Contains(strategyId)) return Array.Empty<StrategyFillDto>();
            return _store.GetFills(strategyId);
        }

        public StrategyFillDto Record(long strategyId, OrderAction action, 
            int quantity, double price, DateTime timeUtc, string orderId, string rawOwnerInfo)
        {
            if (!_allowedIds.Contains(strategyId))
            {
                _logger.Debug(
                    $"ScopedFillsAccess.Record: " +
                    $"attempted to write outside allowed scope, StrategyId={strategyId} - ignored. This indicates a handler isolation bug.");
				
                return null;
            }
            return _store.RecordFill(strategyId, action, quantity, price, timeUtc, orderId, rawOwnerInfo);
        }
    }

    #endregion		
	// ==============================
    #endregion
	// ==========================================================
    #region Strategies.Handlers
	// ==============================
    #region IStrategyAdapter
	
	public interface IStrategyAdapter
	{
	    bool CanHandle(Order order);
	    long GetStrategyId(Order order);
	    string GetStrategyName(Order order);
	    StrategyOrderCategory GetCategory(Order order);
	    StrategyOrderCategory GetCategory(Order order, MarketPosition currentPosition);
	    string GetRawOwnerInfo(Order order);
	    StrategyOrigin GetStrategyOrigin(Order order);
	    OrderType GetOrderType(Order order);
	    OrderAction GetOrderAction(Order order);
	    void EvictOrderFromCache(string orderId);
	    void UpdateSignalNames(string stopNames, string targetNames);
    }

    #endregion
	// ==============================	
    #region StrategyAdapter
		
    public sealed class StrategyAdapter : IStrategyAdapter
    {
	    private const int OrderIdCacheMax = 2000;

	    private readonly BoundedFifoCache<string, long> _orderIdCache = new BoundedFifoCache<string, long>(OrderIdCacheMax);

	    private volatile string[] _nsStopNames = new string[0];
	    private volatile string[] _nsTargetNames = new string[0];

		public bool CanHandle(Order order) { return order != null; }

		public long GetStrategyId(Order order)
		{
		    if (order == null) return StrategyIdentity.UnknownStrategyId;
		       		
		    var owner = order.GetOwnerStrategy();
		    string orderId = order.OrderId?.ToString() ?? string.Empty;
		
		    if (owner != null)
		    {
		        long id = StrategyIdentityCache.GetOrExtract(owner);

		        if (!string.IsNullOrEmpty(orderId) && id != StrategyIdentity.ManualStrategyId)
		            _orderIdCache.Set(orderId, id);
		
		        return id;
		    }

		    if (!string.IsNullOrEmpty(orderId))
		    {
		        if (_orderIdCache.TryGet(orderId, out var cachedId)) return cachedId;		            
		    }
		
		    return StrategyIdentity.ManualStrategyId;
		}

		public string GetStrategyName(Order order)
		{
		    if (order == null) return string.Empty;

		    var owner = order.GetOwnerStrategy();
		
		    if (owner == null) return StrategyIdentity.ManualStrategyName;
		
		    if (owner is AtmStrategy atm)
		    {
		        string displayName = atm.DisplayName ?? atm.Name ?? string.Empty;
		        return displayName;
		    }
		
		    string name = owner.Name ?? owner.ToString() ?? string.Empty;
			
		    return string.IsNullOrWhiteSpace(name) ? StrategyIdentity.UnknownStrategyName : name;
		}

		public StrategyOrderCategory GetCategory(Order order) => GetCategory(order, MarketPosition.Flat);

		public StrategyOrderCategory GetCategory(Order order, MarketPosition currentPosition)
		{
		    if (order == null) return StrategyOrderCategory.Entry;
		    string name = order.Name;
		    return GetCategoryInternal(order, name, currentPosition);
		}
		
		private StrategyOrderCategory GetCategoryInternal(Order order, string name, MarketPosition currentPosition)
		{
		    if (!string.IsNullOrWhiteSpace(name))
		    {
		        if (MatchesAny(name, _nsStopNames)) return StrategyOrderCategory.Stop;
		        if (MatchesAny(name, _nsTargetNames)) return StrategyOrderCategory.Target;				
				
		        if (ContainsIgnoreCase(name, "Entry")) return StrategyOrderCategory.Entry;
		        if (ContainsIgnoreCase(name, "Stop")) return StrategyOrderCategory.Stop;
		        if (ContainsIgnoreCase(name, "Target")) return StrategyOrderCategory.Target;
		
		        if (ContainsIgnoreCase(name, "Exit"))
		        {
		            var ot = order.OrderType;
		            if (ot == OrderType.Limit || ot == OrderType.MIT) return StrategyOrderCategory.Target;
		               
		            return StrategyOrderCategory.Stop;
		        }

		        return StrategyOrderCategory.Entry;
		    }

		    var orderType = order.OrderType;
		    var action = order.OrderAction;
		
		    if (currentPosition == MarketPosition.Flat)
		    {
		        return StrategyOrderCategory.Entry;
		    }
		
		    if (currentPosition == MarketPosition.Short)
		    {
		        if (action == OrderAction.Buy || action == OrderAction.BuyToCover)
		        {
		            if (orderType == OrderType.Limit || orderType == OrderType.MIT) return StrategyOrderCategory.Target;
		               
		            return StrategyOrderCategory.Stop;
		        }
				
		        return StrategyOrderCategory.Entry;
		    }
		    else
		    {
		        if (action == OrderAction.Buy || action == OrderAction.SellShort) return StrategyOrderCategory.Entry;
		           		
		        if (action == OrderAction.Sell || action == OrderAction.BuyToCover)
		        {
		            if (orderType == OrderType.Limit || orderType == OrderType.MIT) return StrategyOrderCategory.Target;
		               
		            return StrategyOrderCategory.Stop;
		        }
		    }
		
		    return StrategyOrderCategory.Entry;
		}

        public string GetRawOwnerInfo(Order order)
        {
            try
            {
                var owner = order?.GetOwnerStrategy();
                if (owner != null) return owner.ToString();

                return StrategyIdentity.ManualStrategyName;
            }
            catch { return string.Empty; } 
        }
		
		public StrategyOrigin GetStrategyOrigin(Order order)
		{
		    try
		    {
		        if (order == null) return StrategyOrigin.UnknownStrategy;
		           		
		        var owner = order.GetOwnerStrategy();
		
		        if (owner == null) return StrategyOrigin.ManualStrategy;
		           
		        long strategyId = GetStrategyId(order);

		        if (StrategyIdentity.IsManualStrategy(strategyId)) return StrategyOrigin.ManualStrategy;
		           
		        if (owner is AtmStrategy) return StrategyOrigin.AtmStrategy;
		           
		        return StrategyOrigin.NinjaScriptStrategy;
		    }
		    catch { return StrategyOrigin.UnknownStrategy; }   
		}

	    public OrderType GetOrderType(Order order)
	    {
	        if (order == null) return OrderType.Market;			
	        return order.OrderType;
	    }	
		
	    public OrderAction GetOrderAction(Order order)
	    {
	        if (order == null) return OrderAction.Buy;			
	        return order.OrderAction;
	    }

	    public void EvictOrderFromCache(string orderId)
	    {
	        if (string.IsNullOrEmpty(orderId)) return;			
	        _orderIdCache.Remove(orderId);
	    }	
	
	    public void UpdateSignalNames(string stopNames, string targetNames)
	    {
	        _nsStopNames = ParseSignalNames(stopNames);
	        _nsTargetNames = ParseSignalNames(targetNames);
	    }
	
	    private static string[] ParseSignalNames(string input)
	    {
	        if (string.IsNullOrWhiteSpace(input)) return new string[0];
			
	        return input
	            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
	            .Select(s => s.Trim())
	            .Where(s => s.Length > 0)
	            .ToArray();
	    }		
				
		private static bool MatchesAny(string name, string[] patterns)
		{
		    foreach (var p in patterns)
		        if (ContainsIgnoreCase(name, p)) return true;				
		    return false;
		}		
		
		private static bool ContainsIgnoreCase(string source, string value)
		{
		    return source.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;		        		       
		}				
    }	

    #endregion
	// ==============================	
    #region IStrategyTypeHandler
	
    internal interface IStrategyTypeHandler
    {
        StrategyOrigin Origin { get; }
        int ReconciliationPhase { get; }

        void ApplyExecution(StrategyExecutionContext ctx);
        bool IsStale(ReconciliationContext ctx, ReconciliationSlice slice);

        bool ShouldHandOffToManual(ReconciliationContext ctx, ReconciliationSlice slice);

        void RefreshOrders(StrategyRefreshOrdersContext ctx);
    }

    #endregion
	// ==============================		
    #region ManualStrategyHandler
	
    internal sealed class ManualStrategyHandler : IStrategyTypeHandler
    {
        private readonly IStrategyLogger _logger;
		
        public ManualStrategyHandler(IStrategyLogger logger)
		{ 
			_logger = logger ?? NullLogger.Instance; 
		}
		
        public StrategyOrigin Origin => StrategyOrigin.ManualStrategy;

        public int ReconciliationPhase => 1;
        // ──────────────────────────────
        #region ApplyExecution

        public void ApplyExecution(StrategyExecutionContext ctx)
        {
            try
            {
                OrderAction execAction = ctx.Order.OrderAction;
                int execQty = Math.Abs(ctx.Execution.Quantity);
                double execPrice = ctx.Execution.Price;
                DateTime execTimeUtc = ctx.Execution.Time.ToUniversalTime();
                string orderIdStr = ctx.Order.OrderId?.ToString() ?? string.Empty;
                string executionIdStr = ctx.Execution.ExecutionId?.ToString() ?? string.Empty;

                if (!ctx.Positions.TryGet(ctx.StrategyId, out var state) || state == null)
                {
                    state = new PositionState(ctx.StrategyId);
                    state.SetOrigin(StrategyOrigin.ManualStrategy);
                    _logger.Debug($"ManualStrategyHandler.ApplyExecution: created Manual state Id={ctx.StrategyId}");                        
                }

                try { ctx.Fills.Record(ctx.StrategyId, execAction, execQty, execPrice, execTimeUtc, orderIdStr, StrategyIdentity.ManualStrategyName); }
                catch (Exception ex) { _logger.Error($"ManualStrategyHandler.ApplyExecution RecordFill error: {ex.Message}"); }

                var prevQty = state.Quantity;
                state.ApplyExecution(execAction, execQty, execPrice, execTimeUtc, ctx.PointValue, executionIdStr);

                PositionMath.SnapAveragePriceToTick(state, ctx.TickSize);

                _logger.Debug(
                    $"ManualStrategyHandler.ApplyExecution: " +
                    $"Action={execAction} Qty={execQty} Price={execPrice} " +
                    $"Qty={prevQty}->{state.Quantity} Dir={state.Direction} Lifecycle={state.LifecycleState}");

                ctx.Positions.Save(ctx.StrategyId, state);
            }
            catch (Exception ex) { _logger.Error($"ManualStrategyHandler.ApplyExecution error: {ex.Message}"); }   
        }

        #endregion
        // ──────────────────────────────
        #region IsStale

        public bool IsStale(ReconciliationContext ctx, ReconciliationSlice slice)
        {
            if (ctx?.PositionState == null || ctx.PositionState.Quantity <= 0) return false;

            if (slice.HasOppositeHedge)
            {
                _logger.Debug(
                    $"ManualStrategyHandler.IsStale: " +
                    $"genuine opposite-sided hedge detected on account " +
                    $"(LiveNet={slice.LiveNetSigned}) - skipping net-signed phantom check Qty={ctx.PositionState.Quantity} Dir={ctx.PositionState.Direction}");
                return false;
            }

            int manualExpected = slice.LiveNetSigned - slice.NonManualSignedSum;
            bool isPhantom = manualExpected == 0;
            if (isPhantom)
                _logger.Debug(
                    $"ManualStrategyHandler.IsStale: " +
                    $"LiveNet={slice.LiveNetSigned} NonManualSum={slice.NonManualSignedSum} manualExpected={manualExpected} " +
                    $"Qty={ctx.PositionState.Quantity} Dir={ctx.PositionState.Direction} - phantom, staging for removal");
                
            return isPhantom;
        }

        #endregion
        // ──────────────────────────────
        #region ShouldHandOffToManual

        public bool ShouldHandOffToManual(ReconciliationContext ctx, ReconciliationSlice slice) => false;

        #endregion
        // ──────────────────────────────
        #region RefreshOrders

        public void RefreshOrders(StrategyRefreshOrdersContext ctx)
        {
            try
            {
                long manualId = StrategyIdentity.ManualStrategyId;

                var entries = new List<StrategyOrderDto>();
                var stops = new List<StrategyOrderDto>();
                var targets = new List<StrategyOrderDto>();

                foreach (var order in ctx.LiveOrders)
                {
                    bool isManualOwner = ctx.GetStrategyId(order) == manualId;

                    if (!isManualOwner) continue;

                    var cat = ctx.GetCategory(order, ctx.ManualPosition);
                    var dto = ctx.CreateDto(manualId, order, cat, ctx.GetRawOwnerInfo(order));
                    if (dto == null) continue;

                    if ((cat & StrategyOrderCategory.Stop) != 0) 
						stops.Add(dto);
                    else if ((cat & StrategyOrderCategory.Target) != 0) 
						targets.Add(dto);
                    else 
						entries.Add(dto);
                }

                if (entries.Count == 0 && stops.Count == 0 && targets.Count == 0)
                    ctx.Orders.Remove(manualId);
                else
                    ctx.Orders.Save(manualId, new StrategyOrderBook(manualId, StrategyIdentity.ManualStrategyName, entries, stops, targets));

                _logger.Debug(
                    $"ManualStrategyHandler.RefreshOrders: " +
                    $"Entries={entries.Count} Stops={stops.Count} Targets={targets.Count}");				
            }
            catch (Exception ex) { _logger.Error($"ManualStrategyHandler.RefreshOrders error: {ex.Message}"); }  
        }

        #endregion
		// ──────────────────────────────
    }

    #endregion
	// ==============================		
    #region AtmStrategyHandler
		
    internal sealed class AtmStrategyHandler : IStrategyTypeHandler
    {
        private readonly IStrategyLogger _logger;
		
		public AtmStrategyHandler(IStrategyLogger logger)
		{ 
			_logger = logger ?? NullLogger.Instance; 
		}
		
        public StrategyOrigin Origin => StrategyOrigin.AtmStrategy;

        public int ReconciliationPhase => 0;

		public void ApplyExecution(StrategyExecutionContext ctx)
		{
		    try
		    {
		        var (state, prevQty) = ExecutionApplicator.Apply(ctx, StrategyOrigin.AtmStrategy);
		
		        _logger.Debug(
		            $"AtmStrategyHandler.ApplyExecution: " +
		            $"Execution applied Qty={prevQty}->{state.Quantity} | Lifecycle={state.LifecycleState}");
				
		        ctx.Positions.Save(ctx.StrategyId, state);
		    }
		    catch (Exception ex) { _logger.Error($"AtmStrategyHandler.ApplyExecution error: {ex.Message}"); }
		}

		public bool IsStale(ReconciliationContext ctx, ReconciliationSlice slice)
		{
		    try
		    {
		        if (ctx?.PositionState == null) return false;
		        var positions = ctx.PositionState;

		        bool hasPosition =
		            positions.Quantity > 0 ||
		            positions.GetSignedQtyForCaching() != 0 ||
		            positions.Direction != MarketPosition.Flat;

		        if (!hasPosition) return false;

		        bool accountFlatNoOrders = !ctx.HasOpenOrders && slice.AccountNet == 0 && !slice.HasOppositeHedge;

		        if (accountFlatNoOrders && positions.Quantity > 0)
		        {
		            _logger.Debug(
		                $"AtmStrategyHandler.IsStale: " +
		                $"ATM={ctx.StrategyId} has no live orders and account is flat (AccountNet=0), " +
		                $"but cache still shows real Quantity={positions.Quantity} - not a phantom, deferring to hand-off to Manual");
		            return false;
		        }

		        if (accountFlatNoOrders)
		        {
		            _logger.Debug(
		                $"AtmStrategyHandler.IsStale: " +
		                $"ATM={ctx.StrategyId} has no live orders and account is flat (AccountNet=0) - cached Quantity={positions.Quantity} is stale");
		            return true;
		        }

		        if (ctx.HasOpenPosition || ctx.HasOpenOrders)
		        {
		            _logger.Debug(
		                $"AtmStrategyHandler.IsStale: " +
		                $"ATM={ctx.StrategyId} still has open position and/or orders - keeping under own ID");
		            return false;
		        }

		        _logger.Debug(
		            $"AtmStrategyHandler.IsStale: " +
		            $"ATM={ctx.StrategyId} closed normally (no position, no orders) -> stale");
		        return true;
		    }
		    catch (Exception ex) { _logger.Error($"AtmStrategyHandler.IsStale error: {ex.Message}"); return false; } 
		}

		public void RefreshOrders(StrategyRefreshOrdersContext ctx){ }

		#region ShouldHandOffToManual

		public bool ShouldHandOffToManual(ReconciliationContext ctx, ReconciliationSlice slice)
		{
		    try
		    {
		        if (ctx?.PositionState == null) return false;
		        var positions = ctx.PositionState;

		        bool hasPosition =
		            positions.Quantity > 0 ||
		            positions.GetSignedQtyForCaching() != 0 ||
		            positions.Direction != MarketPosition.Flat;

		        if (!hasPosition) return false;
		        if (ctx.HasOpenOrders) return false;

		        _logger.Debug(
		            $"AtmStrategyHandler.ShouldHandOffToManual: " +
		            $"ATM={ctx.StrategyId} has no live orders left " +
					$"but still holds Qty={positions.Quantity} Dir={positions.Direction} - handing off to Manual");
		        return true;
		    }
		    catch (Exception ex) { _logger.Error($"AtmStrategyHandler.ShouldHandOffToManual error: {ex.Message}"); return false; }
		}

		#endregion
    }	

    #endregion
	// ==============================		
    #region NinjaScriptStrategyHandler
	
    internal sealed class NinjaScriptStrategyHandler : IStrategyTypeHandler
    {
        private readonly IStrategyLogger _logger;
		
        public NinjaScriptStrategyHandler(IStrategyLogger logger)
		{ 
			_logger = logger ?? NullLogger.Instance; 
		}
		
        public StrategyOrigin Origin => StrategyOrigin.NinjaScriptStrategy;

        public int ReconciliationPhase => 0;

		public void ApplyExecution(StrategyExecutionContext ctx)
		{
		    try
		    {
		        var (state, prevQty) = ExecutionApplicator.Apply(ctx, StrategyOrigin.NinjaScriptStrategy);
		
		        _logger.Debug(
		            $"NinjaScriptStrategyHandler.ApplyExecution: " +
		            $"Execution applied Qty={prevQty}->{state.Quantity} | Lifecycle={state.LifecycleState}");
						
		        ctx.Positions.Save(ctx.StrategyId, state);
		    }
		    catch (Exception ex) { _logger.Error($"NinjaScriptStrategyHandler.ApplyExecution error: {ex.Message}"); }
		}

        public bool IsStale(ReconciliationContext ctx, ReconciliationSlice slice)
        {
            try
            {
                if (ctx?.PositionState == null) return false;
                var positions = ctx.PositionState;

                bool hasPosition =
                    positions.Quantity > 0 ||
                    positions.GetSignedQtyForCaching() != 0 ||
                    positions.Direction != MarketPosition.Flat;

                if (!hasPosition) return false;

                bool hasLiveOrders = ctx.HasOpenOrders;
                bool accountFlatNoOrders = !hasLiveOrders && slice.AccountNet == 0 && !slice.HasOppositeHedge;

                if (!accountFlatNoOrders) return false;

                _logger.Debug(
                    $"NinjaScriptStrategyHandler.IsStale: " +
                    $"NinjaScript {ctx.StrategyId} has no live orders and account is flat " +
                    $"(AccountNet=0) - cached Quantity={positions.Quantity} is stale -> ghost (externally disabled)");
                return true;
            }
            catch (Exception ex) { _logger.Error($"NinjaScriptStrategyHandler.IsStale error: {ex.Message}"); return false; }
        }

		public void RefreshOrders(StrategyRefreshOrdersContext ctx) { }	

		public bool ShouldHandOffToManual(ReconciliationContext ctx, ReconciliationSlice slice) => false;
    }	

    #endregion
	// ==============================
    #endregion
	// ==========================================================
    #region Strategies.Reconciliation
	// ==============================
    #region StrategyAttributionResolver

    internal sealed class StrategyAttributionResolver
    {
        private readonly IStrategyLogger _logger;
        private readonly StrategyPositionStore _positionStore;
        private readonly ReconciliationCoordinator _reconciliation;

        private Dictionary<string, long> _closeOrderBindings = new Dictionary<string, long>(StringComparer.Ordinal);
        private Dictionary<string, int> _closeOrderQty = new Dictionary<string, int>(StringComparer.Ordinal);
        private Dictionary<string, int> _closeOrderRoutedQty = new Dictionary<string, int>(StringComparer.Ordinal);

        public StrategyAttributionResolver(IStrategyLogger logger, StrategyPositionStore positionStore, ReconciliationCoordinator reconciliation)
        {
            _logger = logger ?? NullLogger.Instance;
            _positionStore = positionStore ?? throw new ArgumentNullException(nameof(positionStore));
            _reconciliation = reconciliation ?? throw new ArgumentNullException(nameof(reconciliation));
        }

        public void Clear()
        {
            _closeOrderBindings = new Dictionary<string, long>(StringComparer.Ordinal);
            _closeOrderQty = new Dictionary<string, int>(StringComparer.Ordinal);
            _closeOrderRoutedQty = new Dictionary<string, int>(StringComparer.Ordinal);
        }

        public void RecordCloseFillIfBound(Order order, int fillQty)
        {
            if (order == null || !string.Equals(order.Name, "Close", StringComparison.OrdinalIgnoreCase)) return;

            string oid = order.OrderId?.ToString() ?? string.Empty;
            if (string.IsNullOrEmpty(oid) || !_closeOrderBindings.ContainsKey(oid)) return;

            _closeOrderRoutedQty.TryGetValue(oid, out var already);
            _closeOrderRoutedQty[oid] = already + fillQty;
        }

        public long ResolveCloseExecutionTarget(Order order, Execution execution, long defaultId)
        {
            if (!string.Equals(order.Name, "Close", StringComparison.OrdinalIgnoreCase)) return defaultId;

            string orderIdStr = order.OrderId?.ToString() ?? string.Empty;
            if (!string.IsNullOrEmpty(orderIdStr) && _closeOrderBindings.TryGetValue(orderIdStr, out var boundId))
            {
                _logger.Debug(
                    $"StrategyAttributionResolver.ResolveCloseExecutionTarget: " +
                    $"bound OrderId={order.OrderId} -> Id={boundId} (partial fill routing)");

                return boundId;
            }

            var closingDirection =
                (order.OrderAction == OrderAction.Sell || order.OrderAction == OrderAction.SellShort)
                    ? MarketPosition.Long
                    : MarketPosition.Short;

            int closeQty = Math.Abs(execution.Quantity);

            var states = _positionStore.GetStates();
            var candidates = states.Values
                .Where(s => s != null &&
                            s.IsInitialized &&
                            s.Quantity > 0 &&
                            s.Direction == closingDirection &&
                            s.Id != StrategyIdentity.ManualStrategyId)
                .ToList();

            if (candidates.Count == 0)
            {
                candidates = states.Values
                    .Where(s => s != null &&
                                s.IsInitialized &&
                                s.Direction == closingDirection &&
                                s.Id != StrategyIdentity.ManualStrategyId &&
                                _reconciliation.IsQueuedForCleanup(s.Id))
                    .ToList();

                if (candidates.Count > 0)
                    _logger.Debug(
                        $"StrategyAttributionResolver.ResolveCloseExecutionTarget: " +
                        $"'Close' candidate found in cleanupQueue " +
                        $"(OrderId={order.OrderId}, Direction={closingDirection})");
            }

            var committedQty = new Dictionary<long, int>();
            foreach (var kv in _closeOrderBindings)
            {
                if (kv.Key == orderIdStr) continue;
                int qty = _closeOrderRoutedQty.TryGetValue(kv.Key, out var q) ? q : 1;
                committedQty[kv.Value] = committedQty.TryGetValue(kv.Value, out var c) ? c + qty : qty;
            }

            int GetRemaining(PositionState s) => s.Quantity - (committedQty.TryGetValue(s.Id, out var c) ? c : 0);

            PositionState selected = candidates
                .Where(s => GetRemaining(s) == closeQty)
                .OrderBy(s => s.FirstFillTimeUtc)
                .FirstOrDefault();

            if (selected == null)
            {
                selected = candidates
                    .Where(s => GetRemaining(s) >= closeQty)
                    .OrderBy(s => GetRemaining(s) - closeQty)
                    .ThenBy(s => s.FirstFillTimeUtc)
                    .FirstOrDefault();
            }

            if (selected == null)
            {
                selected = candidates
                    .Where(s => GetRemaining(s) > 0)
                    .OrderByDescending(s => GetRemaining(s))
                    .ThenBy(s => s.FirstFillTimeUtc)
                    .FirstOrDefault();

                if (selected != null)
                    _logger.Debug(
                        $"StrategyAttributionResolver.ResolveCloseExecutionTarget: " +
                        $"no single candidate covers " +
                        $"closeQty={closeQty} - routing to largest remaining: Id={selected.Id} remaining={GetRemaining(selected)}");
            }

            if (selected == null) return defaultId;

            if (!string.IsNullOrEmpty(orderIdStr))
            {
                _closeOrderBindings[orderIdStr] = selected.Id;
                _closeOrderQty[orderIdStr] = closeQty;
                _closeOrderRoutedQty.TryGetValue(orderIdStr, out var alreadyRouted);
                _closeOrderRoutedQty[orderIdStr] = alreadyRouted + closeQty;

                _logger.Debug(
                    $"StrategyAttributionResolver.ResolveCloseExecutionTarget: " +
                    $"bound OrderId={order.OrderId} -> Id={selected.Id} " +
                    $"(Direction={closingDirection}, CloseQty={closeQty}, StratQty={selected.Quantity})");
            }

            _logger.Debug(
                $"StrategyAttributionResolver.ResolveCloseExecutionTarget: " +
                $"routing 'Close' execution (OrderId={order.OrderId}) " +
                $"to Id={selected.Id} instead of Manual (closing its {closingDirection} Qty={selected.Quantity} position)");

            return selected.Id;
        }

        public void PruneCloseOrderBindings(Account account, Instrument instrument)
        {
            if (_closeOrderBindings.Count == 0) return;

            var liveOrderIds = new HashSet<string>(StringComparer.Ordinal);
            if (account?.Orders != null)
            {
                foreach (var o in account.Orders)
                {
                    if (o == null || o.Instrument == null) continue;
                    if (!string.Equals(o.Instrument.FullName, instrument?.FullName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (Order.IsTerminalState(o.OrderState)) continue;

                    if (!string.IsNullOrEmpty(o.OrderId))
                        liveOrderIds.Add(o.OrderId);
                }
            }

            var toRemove = new List<string>();
            foreach (var key in _closeOrderBindings.Keys)
            {
                if (liveOrderIds.Contains(key)) continue;

                _closeOrderRoutedQty.TryGetValue(key, out var routedQty);
                if (routedQty == 0)
                {
                    _logger.Debug(
                        $"StrategyAttributionResolver.PruneCloseOrderBindings: " +
                        $"OrderId={key} terminal but routedQty=0 - keeping binding (execution pending)");

                    continue;
                }

                toRemove.Add(key);
            }

            foreach (var key in toRemove)
            {
                _logger.Debug($"StrategyAttributionResolver.PruneCloseOrderBindings: removed binding for OrderId={key}");
                _closeOrderBindings.Remove(key);
                _closeOrderQty.Remove(key);
                _closeOrderRoutedQty.Remove(key);
            }
        }

        public void ReclassifyManualOrders(PositionState positionState,
            ref IReadOnlyList<StrategyOrderDto> entries, ref IReadOnlyList<StrategyOrderDto> stops, ref IReadOnlyList<StrategyOrderDto> targets)
        {
            var freshEntries = new List<StrategyOrderDto>(entries);
            var freshStops = new List<StrategyOrderDto>();
            var freshTargets = new List<StrategyOrderDto>();

            foreach (var o in stops.Concat(targets))
            {
                if (ActionClosesPosition(o.Action, positionState.Direction))
                {
                    if ((o.Category & StrategyOrderCategory.Stop) != 0)
                        freshStops.Add(o);
                    else
                        freshTargets.Add(o);
                }
                else
                {
                    freshEntries.Add(WithCategory(o, StrategyOrderCategory.Entry));
                }
            }

            entries = freshEntries;
            stops = freshStops;
            targets = freshTargets;
        }

        private static bool ActionClosesPosition(OrderAction action, MarketPosition position)
        {
            if (position == MarketPosition.Short)
                return action == OrderAction.Buy || action == OrderAction.BuyToCover;
            if (position == MarketPosition.Long)
                return action == OrderAction.Sell;
            return true;
        }

        private static StrategyOrderDto WithCategory(StrategyOrderDto dto, StrategyOrderCategory newCategory)
        {
            if (dto == null) return null;
            if (dto.Category == newCategory) return dto;
            return new StrategyOrderDto(
                dto.StrategyId,
                dto.OrderId,
                dto.OrderName,
                dto.QuantityOrders,
                dto.LimitPrice,
                dto.StopPrice,
                dto.State,
                newCategory,
                dto.RawOwnerInfo,
                dto.Origin,
                dto.OrderType,
                dto.Action,
                dto.CreatedOrderTime);
        }
    }

    #endregion
	// ==============================
    #region ReconciliationCoordinator

    internal sealed class ReconciliationCoordinator
    {
        private const int GracePeriodMs = 3000;
        private const int MaxCleanupAgeMs = 6000;

        private readonly IStrategyLogger _logger;
        private readonly StrategyOrderBookStore _orderBook;
        private readonly StrategyPositionStore _positionStore;
        private readonly StrategyFillsStore _fillsStore;
        private readonly Dictionary<StrategyOrigin, IStrategyTypeHandler> _handlers;
        private readonly IStrategyAdapter _adapter;
        private readonly Func<long, Order, StrategyOrderCategory, string, StrategyOrderDto> _createDto;

        private volatile Dictionary<long, StrategyCleanupContext> _cleanupQueue = new Dictionary<long, StrategyCleanupContext>();
        // ──────────────────────────────
        #region Constructor

        public ReconciliationCoordinator(
            IStrategyLogger logger,
            StrategyOrderBookStore orderBook,
            StrategyPositionStore positionStore,
            StrategyFillsStore fillsStore,
            Dictionary<StrategyOrigin, IStrategyTypeHandler> handlers,
            IStrategyAdapter adapter,
            Func<long, Order, StrategyOrderCategory, string, StrategyOrderDto> createDto)
        {
            _logger = logger ?? NullLogger.Instance;
            _orderBook = orderBook ?? throw new ArgumentNullException(nameof(orderBook));
            _positionStore = positionStore ?? throw new ArgumentNullException(nameof(positionStore));
            _fillsStore = fillsStore ?? throw new ArgumentNullException(nameof(fillsStore));
            _handlers = handlers ?? throw new ArgumentNullException(nameof(handlers));
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
            _createDto = createDto ?? throw new ArgumentNullException(nameof(createDto));
        }

        #endregion
        // ──────────────────────────────
        #region Cleanup queue - public surface

        public bool IsQueuedForCleanup(long strategyId) => _cleanupQueue.ContainsKey(strategyId);

        public ICollection<long> QueuedStrategyIds => _cleanupQueue.Keys;

        public void MarkForCleanup(long strategyId, string reason = null)
        {
            var current = _cleanupQueue;
            if (current.ContainsKey(strategyId)) return;

            var updated = new Dictionary<long, StrategyCleanupContext>(current)
            {
                [strategyId] = new StrategyCleanupContext(strategyId) { MarkedForRemovalAt = DateTime.UtcNow }
            };
            _cleanupQueue = updated;

			_logger.Debug(
			    $"ReconciliationCoordinator.MarkForCleanup: " +
			    $"Strategy {strategyId} queued for cleanup {(string.IsNullOrEmpty(reason) ? string.Empty : $" ({reason})")}");
        }

        public void Unmark(long strategyId)
        {
            var current = _cleanupQueue;
            if (!current.ContainsKey(strategyId)) return;

            var updated = new Dictionary<long, StrategyCleanupContext>(current);
            updated.Remove(strategyId);
            _cleanupQueue = updated;
        }

        public void Clear()
        {
            _cleanupQueue = new Dictionary<long, StrategyCleanupContext>();
        }

        #endregion
        // ──────────────────────────────
        #region Main pass

        public void Run(Account account, Instrument instrument, int accountNet)
        {
            try
            {
                var allContexts = BuildReconciliationContexts();

                var livePos = PositionMath.BuildAccountNetPositionSnapshotStatic(account, instrument);
                int liveNet = livePos.HasPosition
                    ? (livePos.MarketPosition == MarketPosition.Long ? livePos.QuantityPositions : -livePos.QuantityPositions)
                    : 0;

                if (accountNet != liveNet)
                {
                    _logger.Debug(
                        $"ReconciliationCoordinator.Run: " +
                        $"accountNet param ({accountNet}) is stale vs live ({liveNet}) - using live value");
                    accountNet = liveNet;
                }

                bool hasOppositeHedge = PositionMath.AccountHasGenuineOppositeSidedPositions(account, instrument);
                var cleanupQueueIdsBefore = new HashSet<long>(_cleanupQueue.Keys);

                var byOrigin = new Dictionary<StrategyOrigin, List<ReconciliationContext>>();
                foreach (var ctx in allContexts)
                {
                    var origin = ctx?.PositionState?.Origin ?? StrategyOrigin.ManualStrategy;
                    if (StrategyIdentity.IsManualStrategy(ctx?.StrategyId ?? 0))
                        origin = StrategyOrigin.ManualStrategy;
                    if (!byOrigin.TryGetValue(origin, out var list))
                        byOrigin[origin] = list = new List<ReconciliationContext>();
                    list.Add(ctx);
                }

                var staleIds = new HashSet<long>();

                foreach (var kv in byOrigin)
                {
                    if (_handlers.ContainsKey(kv.Key)) continue;

                    foreach (var orphanCtx in kv.Value)
                    {
                        if (orphanCtx == null) continue;
                        staleIds.Add(orphanCtx.StrategyId);
                        _logger.Debug(
                            $"ReconciliationCoordinator.Run: " +
                            $"No handler for origin={kv.Key} StrategyId={orphanCtx.StrategyId} - quarantining orphaned state.");
                    }
                }

                foreach (var id in staleIds)
                    RemoveStrategyCompletely(id, account, instrument);

                var phaseGroups = _handlers.Values
                    .Where(h => byOrigin.ContainsKey(h.Origin))
                    .GroupBy(h => h.ReconciliationPhase)
                    .OrderBy(g => g.Key);

                foreach (var phaseGroup in phaseGroups)
                {
                    var phaseStaleIds = new HashSet<long>();
                    var phaseHandoffIds = new HashSet<long>();

                    int nonManualSignedSumForPhase = _positionStore.GetStates().Values
                        .Where(s => s != null && s.IsInitialized && s.Id != StrategyIdentity.ManualStrategyId
                                 && _handlers.ContainsKey(s.Origin))
                        .Sum(s => s.Direction == MarketPosition.Long ? s.Quantity : s.Direction == MarketPosition.Short ? -s.Quantity : 0);

                    foreach (var handler in phaseGroup)
                    {
                        var contexts = byOrigin[handler.Origin];

                        var slice = new ReconciliationSlice
                        {
                            Contexts = contexts,
                            Account = account,
                            Instrument = instrument,
                            AccountNet = accountNet,
                            Orders = new ScopedOrdersAccess(_orderBook, new HashSet<long>(contexts.Select(c => c.StrategyId)), _logger),
                            CleanupQueueIds = cleanupQueueIdsBefore,
                            LiveNetSigned = liveNet,
                            NonManualSignedSum = nonManualSignedSumForPhase,
                            HasOppositeHedge = hasOppositeHedge,
                        };

                        foreach (var ctx in contexts)
                        {
                            if (ctx == null) continue;

                            if (handler.IsStale(ctx, slice))
                            {
                                phaseStaleIds.Add(ctx.StrategyId);
                                continue;
                            }

                            if (handler.ShouldHandOffToManual(ctx, slice))
                                phaseHandoffIds.Add(ctx.StrategyId);
                        }
                    }

                    foreach (var id in phaseStaleIds)
                        RemoveStrategyCompletely(id, account, instrument);

                    foreach (var id in phaseHandoffIds)
                        TransferStrategyToManual(id, account, instrument);

                    staleIds.UnionWith(phaseStaleIds);
                    staleIds.UnionWith(phaseHandoffIds);
                }

                if (_handlers.TryGetValue(StrategyOrigin.ManualStrategy, out var manualRefreshHandler))
                {
                    var manualAllowedIds = new HashSet<long> { StrategyIdentity.ManualStrategyId };
                    var liveOrders = PositionMath.GetLiveOrdersForInstrument(account, instrument);
                    var refreshCtx = new StrategyRefreshOrdersContext
                    {
                        Account = account,
                        Instrument = instrument,
                        Orders = new ScopedOrdersAccess(_orderBook, manualAllowedIds, _logger),
                        LiveOrders = liveOrders,
                        ManualPosition = GetManualPosition(account, instrument),
                        GetCategory = (o, pos) => _adapter.GetCategory(o, pos),
                        GetStrategyId = o => _adapter.GetStrategyId(o),
                        GetRawOwnerInfo = o => _adapter.GetRawOwnerInfo(o),
                        CreateDto = (sid, o, cat, raw) => _createDto(sid, o, cat, raw),
                    };

                    manualRefreshHandler.RefreshOrders(refreshCtx);
                }

                var remainingContexts = allContexts.Where(c => c != null && !staleIds.Contains(c.StrategyId)).ToList();
                MarkFullyClosedStrategiesForCleanup(remainingContexts);
                CleanupClosedStrategies(account, instrument);
            }
            catch (Exception ex) { _logger.Error($"ReconciliationCoordinator.Run error: {ex.Message}"); }
        }

        private List<ReconciliationContext> BuildReconciliationContexts()
        {
            var contexts = new List<ReconciliationContext>();
            var currentPositions = _positionStore.GetStates();
            var currentOrders = _orderBook.GetAll();

            var allStrategyIds = new HashSet<long>(currentPositions.Keys);
            allStrategyIds.UnionWith(currentOrders.Keys);

            foreach (var strategyId in allStrategyIds)
            {
                currentPositions.TryGetValue(strategyId, out var positions);
                currentOrders.TryGetValue(strategyId, out var orders);

                contexts.Add(new ReconciliationContext(strategyId, positions, orders));
            }

            return contexts;
        }

        private MarketPosition GetManualPosition(Account account, Instrument instrument)
        {
            MarketPosition pos = MarketPosition.Flat;
            if (_positionStore.TryGetState(StrategyIdentity.ManualStrategyId, out var manualRt))
                pos = manualRt.Direction;

            if (pos == MarketPosition.Flat)
            {
                var accountPos = account.Positions?.FirstOrDefault(p => p?.Instrument == instrument);
                if (accountPos != null) pos = accountPos.MarketPosition;
            }
            return pos;
        }

        #endregion
        // ──────────────────────────────
        #region Transfer to Manual

        private void TransferStrategyToManual(long sourceStrategyId, Account account, Instrument instrument)
        {
            try
            {
                if (!_positionStore.TryGetState(sourceStrategyId, out var sourceState) || sourceState == null || sourceState.Quantity <= 0)
                {
                    RemoveStrategyCompletely(sourceStrategyId, account, instrument);
                    return;
                }

                long manualId = StrategyIdentity.ManualStrategyId;
                _positionStore.TryGetState(manualId, out var existingManualState);

                var mergedManualState = (existingManualState != null && existingManualState.Quantity > 0)
                    ? MergeIntoManualState(existingManualState, sourceState)
                    : CloneAsManualState(sourceState);

                mergedManualState.SetOrigin(StrategyOrigin.ManualStrategy);

                var mergedFills = (_fillsStore.GetFills(manualId) ?? Array.Empty<StrategyFillDto>())
                    .Concat(_fillsStore.GetFills(sourceStrategyId) ?? Array.Empty<StrategyFillDto>())
                    .OrderBy(f => f.TimeUtc)
                    .ToList();
                if (mergedFills.Count > 0)
                    _fillsStore.RestoreFills(manualId, mergedFills);

                _positionStore.SaveState(manualId, account, instrument, mergedManualState);

                _orderBook.Remove(sourceStrategyId);
                _positionStore.RemoveState(sourceStrategyId, account, instrument);

                Unmark(sourceStrategyId);

                _logger.Debug(
                    $"ReconciliationCoordinator.TransferStrategyToManual: " +
                    $"StrategyId={sourceStrategyId} handed off to Manual | " +
                    $"Source Qty={sourceState.Quantity} Dir={sourceState.Direction} Avg={sourceState.AveragePrice:F6} -> " +
                    $"Manual Qty={mergedManualState.Quantity} Dir={mergedManualState.Direction} Avg={mergedManualState.AveragePrice:F6}");
            }
            catch (Exception ex) { _logger.Error($"ReconciliationCoordinator.TransferStrategyToManual error: {ex.Message}"); }
        }

        private static PositionState CloneAsManualState(PositionState source)
        {
            var clone = new PositionState(StrategyIdentity.ManualStrategyId);
            clone.RestoreFromCache(
                source.GetSignedQtyForCaching(),
                source.Quantity,
                source.AveragePrice,
                source.Direction,
                source.FirstFillTimeUtc,
                source.LastExecutionTimeUtc);
            return clone;
        }

        private static PositionState MergeIntoManualState(PositionState manual, PositionState incoming)
        {
            long manualSigned = manual.GetSignedQtyForCaching();
            long incomingSigned = incoming.GetSignedQtyForCaching();
            long mergedSigned = manualSigned + incomingSigned;

            bool sameSide = (manualSigned >= 0 && incomingSigned >= 0) || (manualSigned <= 0 && incomingSigned <= 0);

            double mergedAvg = 0.0;
            if (mergedSigned == 0)
            {
                mergedAvg = 0.0;
            }
            else if (sameSide)
            {
                long absManual = Math.Abs(manualSigned);
                long absIncoming = Math.Abs(incomingSigned);
                if (absManual + absIncoming > 0)
                    mergedAvg = (manual.AveragePrice * absManual + incoming.AveragePrice * absIncoming) / (absManual + absIncoming);
                else
                    mergedAvg = incoming.AveragePrice;
            }
            else
            {
                if (Math.Abs(manualSigned) >= Math.Abs(incomingSigned))
                    mergedAvg = manual.AveragePrice;
                else
                    mergedAvg = incoming.AveragePrice;
            }

            MarketPosition mergedDirection;
            if (mergedSigned > 0) mergedDirection = MarketPosition.Long;
            else if (mergedSigned < 0) mergedDirection = MarketPosition.Short;
            else mergedDirection = MarketPosition.Flat;

            DateTime emptyTime = default(DateTime);
            DateTime mergedFirstFill = (manual.FirstFillTimeUtc != emptyTime && manual.FirstFillTimeUtc < incoming.FirstFillTimeUtc)
                ? manual.FirstFillTimeUtc
                : incoming.FirstFillTimeUtc;

            DateTime mergedLastExec = (manual.LastExecutionTimeUtc > incoming.LastExecutionTimeUtc)
                ? manual.LastExecutionTimeUtc
                : incoming.LastExecutionTimeUtc;

            var merged = new PositionState(StrategyIdentity.ManualStrategyId);
            merged.RestoreFromCache(mergedSigned, (int)Math.Abs(mergedSigned), mergedAvg, mergedDirection, mergedFirstFill, mergedLastExec);
            return merged;
        }

        #endregion
        // ──────────────────────────────
        #region Remove / Cleanup

        private void RemoveStrategyCompletely(long strategyId, Account account, Instrument instrument)
        {
            try
            {
                _positionStore.RemoveState(strategyId, account, instrument);

                bool hasProtectiveOrders =
                    _orderBook.TryGet(strategyId, out var localOrders) &&
                    (((localOrders?.Stops?.Count ?? 0) > 0) || ((localOrders?.Targets?.Count ?? 0) > 0));

                if (!hasProtectiveOrders)
                    _orderBook.Remove(strategyId);
                else
                    _logger.Debug(
                        $"ReconciliationCoordinator.RemoveStrategyCompletely: " +
                        $"StrategyId={strategyId} removed but keeping orphaned " +
                        $"Stop/Target orders (Stops={localOrders?.Stops?.Count ?? 0} Targets={localOrders?.Targets?.Count ?? 0})");

                Unmark(strategyId);

                _logger.Debug($"ReconciliationCoordinator.RemoveStrategyCompletely: Strategy {strategyId} removed from positionStore");
            }
            catch (Exception ex) { _logger.Error($"ReconciliationCoordinator.RemoveStrategyCompletely error: {ex.Message}"); }
        }

        private void MarkFullyClosedStrategiesForCleanup(List<ReconciliationContext> contexts)
        {
            try
            {
                var current = _cleanupQueue;
                var updated = new Dictionary<long, StrategyCleanupContext>(current);

                foreach (var ctx in contexts)
                {
                    if (ctx == null) continue;

                    if (ctx.IsFullyClosed)
                    {
                        if (!updated.ContainsKey(ctx.StrategyId))
                        {
                            var cleanupCtx = new StrategyCleanupContext(ctx.StrategyId) { MarkedForRemovalAt = DateTime.UtcNow };
                            updated[ctx.StrategyId] = cleanupCtx;
                            _logger.Debug(
                                $"ReconciliationCoordinator.MarkFullyClosedStrategiesForCleanup: " +
                                $"Strategy {ctx.StrategyId} marked for cleanup");
                        }
                    }
                    else
                    {
                        if (updated.ContainsKey(ctx.StrategyId))
                        {
                            updated.Remove(ctx.StrategyId);
                            _logger.Debug(
                                $"ReconciliationCoordinator.MarkFullyClosedStrategiesForCleanup: " +
                                $"Strategy {ctx.StrategyId} unmarked from cleanup");
                        }
                    }
                }

                _cleanupQueue = updated;
            }
            catch (Exception ex) { _logger.Error($"ReconciliationCoordinator.MarkFullyClosedStrategiesForCleanup error: {ex.Message}"); }
        }

        private void CleanupClosedStrategies(Account account, Instrument instrument)
        {
            try
            {
                var current = _cleanupQueue;
                var toRemove = new List<long>();
                var overdue = new List<long>();

                foreach (var kv in current)
                {
                    if (kv.Value.IsOverdue(MaxCleanupAgeMs))
                    {
                        overdue.Add(kv.Key);
                        _logger.Debug(
                            $"ReconciliationCoordinator.CleanupClosedStrategies: " +
                            $"Strategy {kv.Key} overdue in cleanup queue " +
                            $"({DateTime.UtcNow - kv.Value.MarkedForRemovalAt:mm\\:ss}). Force removing to prevent memory leak.");
                    }
                    else if (kv.Value.IsReadyForRemoval(GracePeriodMs))
                    {
                        toRemove.Add(kv.Key);
                    }
                }

                toRemove.AddRange(overdue);

                foreach (var strategyId in toRemove)
                {
                    try { RemoveStrategyCompletely(strategyId, account, instrument); }
                    catch (Exception ex) { _logger.Error($"ReconciliationCoordinator.CleanupClosedStrategies Cleanup strategy {strategyId} error: {ex.Message}"); }
                }
            }
            catch (Exception ex) { _logger.Error($"ReconciliationCoordinator.CleanupClosedStrategies error: {ex.Message}"); }
        }

        #endregion
        // ──────────────────────────────
    }

    #endregion	
	// ==============================
    #region ExecutionApplicator

	internal static class ExecutionApplicator
	{
	    public static (PositionState state, int prevQty) Apply(StrategyExecutionContext ctx, StrategyOrigin origin) 
	    {
	        var order = ctx.Order;
	        var execution = ctx.Execution;
	
	        OrderAction  execAction = order.OrderAction;
	        int execQty = Math.Abs(execution.Quantity);
	        double execPrice = execution.Price;
	        DateTime execTimeUtc = execution.Time.ToUniversalTime();
	        string orderIdString = order.OrderId?.ToString() ?? string.Empty;
	        string executionIdStr = execution.ExecutionId?.ToString() ?? string.Empty;
	        string fillOwnerInfo = ctx.RawOwnerInfo ?? string.Empty;
	
	        bool isNew = !ctx.Positions.TryGet(ctx.StrategyId, out var state) || state == null;
	        if (isNew)
	        {
	            state = new PositionState(ctx.StrategyId);
	            state.SetOrigin(origin);
	        }
	        else
	        {
	            bool demotion = origin == StrategyOrigin.ManualStrategy && 
					(state.Origin == StrategyOrigin.AtmStrategy || state.Origin == StrategyOrigin.NinjaScriptStrategy);       
	            if (!demotion)
	                state.SetOrigin(origin);
	        }
	
	        if (!string.IsNullOrWhiteSpace(ctx.StrategyName))
	            state.SetKnownName(ctx.StrategyName);
	
	        ctx.Fills.Record(ctx.StrategyId, execAction, execQty, execPrice, execTimeUtc, orderIdString, fillOwnerInfo);
	
	        int prevQty = state.Quantity;
	        state.ApplyExecution(execAction, execQty, execPrice, execTimeUtc, ctx.PointValue, executionIdStr);
	        PositionMath.SnapAveragePriceToTick(state, ctx.TickSize);
	
	        return (state, prevQty);
	    }
	}

    #endregion
	// ==============================		
    #region ReconciliationSlice

    internal sealed class ReconciliationSlice
    {
        public IReadOnlyList<ReconciliationContext> Contexts { get; set; }
        public Account Account { get; set; }
        public Instrument Instrument { get; set; }
        public int AccountNet { get; set; }
        public IStrategyOrdersAccess Orders { get; set; }
        public HashSet<long> CleanupQueueIds { get; set; } = new HashSet<long>();
        public int LiveNetSigned { get; set; }
        public int NonManualSignedSum { get; set; }
        public bool HasOppositeHedge { get; set; }
    }

    #endregion
	// ==============================	
    #region ReconciliationContext	
	
    public sealed class ReconciliationContext
    {
        private const int IdleTimeoutMs = 2000;
		
        public long StrategyId { get; }
        public PositionState PositionState { get; }
        public StrategyOrderBook Orders { get; }
        
        public bool HasOpenOrders { get; }
        public bool HasOpenPosition { get; }
        public bool IsIdle { get; }
        public bool IsFullyClosed { get; }

        public ReconciliationContext(long strategyId, PositionState positions, StrategyOrderBook orders)
        {
            StrategyId = strategyId;
            PositionState = positions;
            Orders = orders;

			HasOpenOrders = orders != null && 
			    (orders.Entries.Count > 0 
				    || orders.Stops.Count > 0 
				    || orders.Targets.Count > 0) && 
			    (orders.Entries.Any(o => !IsTerminalOrderState(o.State)) 
				    || orders.Stops.Any(o => !IsTerminalOrderState(o.State)) 
				    || orders.Targets.Any(o => !IsTerminalOrderState(o.State)));

            HasOpenPosition = positions != null && positions.Quantity > 0 && positions.IsInitialized; 
            IsIdle = positions != null && (DateTime.UtcNow - positions.LastActivityUtc) > TimeSpan.FromMilliseconds(IdleTimeoutMs); 
            IsFullyClosed = !HasOpenOrders && !HasOpenPosition && IsIdle && (positions?.LifecycleState == StrategyLifecycleState.Closed); 
        }
		
		private static bool IsTerminalOrderState(OrderState state)
		{
		    return state == OrderState.Filled ||
		           state == OrderState.Cancelled ||
		           state == OrderState.Rejected ||
		           state == OrderState.Unknown ||
		           state == OrderState.CancelSubmitted;
		}		

        public override string ToString()
        {
            return $"Strategy={StrategyId} | " +
                   $"HasOrders={HasOpenOrders} | " +
                   $"HasPosition={HasOpenPosition} | " +
                   $"IsIdle={IsIdle} | " +
                   $"IsFullyClosed={IsFullyClosed} | " +
                   $"Lifecycle={PositionState?.LifecycleState ?? StrategyLifecycleState.Pending}";
        }
    }
	
    #endregion	
	// ==============================	
    #region StrategyExecutionContext	

    internal sealed class StrategyExecutionContext
    {
        public Account Account { get; set; }
        public Instrument Instrument { get; set; }
        public Execution Execution { get; set; }
        public Order Order { get; set; }
        public long StrategyId { get; set; }
        public double PointValue { get; set; }
        public double TickSize { get; set; }
        public string RawOwnerInfo { get; set; }
        public string StrategyName { get; set; }
        public IStrategyPositionsAccess Positions { get; set; }
        public IStrategyOrdersAccess Orders { get; set; }
        public IStrategyFillsAccess Fills { get; set; }
    }
	
    #endregion	
	// ==============================		
    #region StrategyRefreshOrdersContext	

    internal sealed class StrategyRefreshOrdersContext
    {
        public Account Account { get; set; }
        public Instrument Instrument { get; set; }
        public IStrategyOrdersAccess Orders { get; set; }
        public IReadOnlyList<Order> LiveOrders { get; set; }
        public MarketPosition ManualPosition { get; set; }
        public Func<Order, MarketPosition, StrategyOrderCategory> GetCategory { get; set; }
        public Func<Order, long> GetStrategyId { get; set; }
        public Func<Order, string> GetRawOwnerInfo { get; set; }
        public Func<long, Order, StrategyOrderCategory, string, StrategyOrderDto> CreateDto { get; set; }
    }
	
    #endregion	
	// ==============================
    #region StrategyCleanupContext

    public sealed class StrategyCleanupContext
    {
        public long StrategyId { get; }
        public DateTime MarkedForRemovalAt { get; set; }

        public StrategyCleanupContext(long strategyId)
        {
            StrategyId = strategyId;
            MarkedForRemovalAt = DateTime.MinValue;
        }

        public bool IsReadyForRemoval(int gracePeriodMs)
        {
            if (MarkedForRemovalAt == DateTime.MinValue) return false;
            return DateTime.UtcNow - MarkedForRemovalAt >= TimeSpan.FromMilliseconds(gracePeriodMs);
        }

        public bool IsOverdue(int maxCleanupAgeMs)
        {
            if (MarkedForRemovalAt == DateTime.MinValue) return false;
            return DateTime.UtcNow - MarkedForRemovalAt >= TimeSpan.FromMilliseconds(maxCleanupAgeMs);
        }

        public TimeSpan TimeSinceMarked => DateTime.UtcNow - MarkedForRemovalAt;
    }

    #endregion
	// ==============================
    #endregion
	// ==========================================================
    #region Strategies.SnapshotEngine
	// ==============================
    public sealed class StrategySnapshotEngine : IDisposable
    {
	    private const int DebounceMs = 100;
		
        private static readonly StrategyCalculationMode[] AllModes = { StrategyCalculationMode.Personal, StrategyCalculationMode.Aggregated };
		
		private StrategyCalculationMode _strategyMode = StrategyCalculationMode.Personal;

	    private readonly IStrategyAdapter _adapter;
	    private readonly StrategyExecutionStore _executionStore;
	    private readonly Func<Func<Task>, DispatcherPriority, Task> _runOnUiThreadAsync;
	    private readonly IStrategyLogger _logger;
		private readonly StrategyFillsStore _fillsStore;
		private readonly StrategyPositionStore _positionStore;
		private readonly StrategyDebouncer _debouncer;
		private readonly Dictionary<StrategyOrigin, IStrategyTypeHandler> _handlers;
		
        private readonly StrategyOrderBookStore _orderBook = new StrategyOrderBookStore();
        private readonly ChangeDetectingCache<StrategyCalculationMode, StrategySnapshot> _snapshotCache =
            new ChangeDetectingCache<StrategyCalculationMode, StrategySnapshot>(AllModes, StrategySnapshot.Empty);
		
		private volatile bool _initialConsistencyCheckDone;				
		private volatile bool _awaitingInitialFlat;
        private readonly ReconciliationCoordinator _reconciliation;
        private readonly StrategyAttributionResolver _attribution;

        private long _snapshotVersion = 0;

		private double _lastAggAveragePrice = 0.0;
		private MarketPosition _lastAggDirection = MarketPosition.Flat;
	    // ──────────────────────────────		
        #region Constructor

		public StrategySnapshotEngine(SlotKey slot, IStrategyAdapter adapter, StrategyExecutionStore executionStore, 
			Func<Func<Task>, DispatcherPriority, Task> runOnUiThreadAsync, IStrategyLogger logger)
		{
		    _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
		    _executionStore = executionStore ?? throw new ArgumentNullException(nameof(executionStore));		       
		    _runOnUiThreadAsync = runOnUiThreadAsync ?? throw new ArgumentNullException(nameof(runOnUiThreadAsync));
		    _logger = logger ?? NullLogger.Instance;		
	        _fillsStore = new StrategyFillsStore(_logger);
	        _positionStore = new StrategyPositionStore(slot, _logger, _fillsStore);
		    _debouncer = new StrategyDebouncer(
				DebounceMs, (acc, instr, sId, pv, ts) => RefreshSingleStrategyAndPublish(acc, instr, sId, pv, ts), _runOnUiThreadAsync, _logger);
		    _handlers = BuildHandlers();
		    _reconciliation = new ReconciliationCoordinator(
				_logger, _orderBook, _positionStore, _fillsStore, _handlers, _adapter,
				(sid, o, cat, raw) => CreateDto(sid, o, cat, raw));
		    _attribution = new StrategyAttributionResolver(_logger, _positionStore, _reconciliation);
		}

	    #endregion		
	    // ──────────────────────────────
        #region Public API

		public StrategyCalculationMode StrategyMode
		{
		    get => _strategyMode;
		    set
		    {
		        if (_strategyMode == value) return;
		        _strategyMode = value;

				var snap = GetSnapshotInternal(value);
				try { SnapshotChanged?.Invoke(snap); } catch { /* Ignore */ }
		    }
		}

        public StrategySnapshot CurrentSnapshot => GetSnapshotInternal(StrategyMode);

        public StrategySnapshot GetSnapshot(StrategyCalculationMode mode) => GetSnapshotInternal(mode);

        public event Action<StrategySnapshot> SnapshotChanged;

        public event Action<StrategyCalculationMode, StrategySnapshot> SnapshotChangedForMode;

		public IReadOnlyDictionary<long, PositionState> GetPositionStatesSnapshot()
		{ 
			return _positionStore.GetSnapshot(); 
		}

		public long GetStrategyId(Order order) => _adapter.GetStrategyId(order);

		public bool IsSimpleOwnerlessOrder(long strategyId)
		{
		    try { if (!StrategyIdentity.IsManualStrategy(strategyId)) return false; return true; }
		    catch (Exception ex) { _logger.Error($"StrategySnapshotEngine.IsSimpleOwnerlessOrder error: {ex.Message}"); return false; } 
		}

		public Task BuildFullSnapshotAsync(Account account, Instrument instrument, double pointValue, double tickSize)
		{
		    return RunOnUiOrInline(() => FullRebuildAndPublish(account, instrument, pointValue, tickSize));
		}

        public Task ProcessExecution(Account account, Instrument instrument, Execution execution, double pointValue, double tickSize)
        {
            if (execution == null) return Task.CompletedTask;

		    return RunOnUiOrInline(() =>
		    {
		        ProcessExecutionInternal(account, instrument, execution, pointValue, tickSize); 
				return Task.CompletedTask;		       
		    });
        }

		public Task ReconcileAndRefreshAsync(Account account, Instrument instrument, double pointValue, double tickSize, int accountNet)
		{
		    if (!_initialConsistencyCheckDone) return BuildFullSnapshotAsync(account, instrument, pointValue, tickSize);

		    return RunOnUiOrInline(() =>
		    {
		        try
		        {
					_reconciliation.Run(account, instrument, accountNet);
					ComposeSnapshotAndPublishGated(account, instrument, pointValue, tickSize);
		        }
		        catch (Exception ex) { _logger.Error($"StrategySnapshotEngine.ReconcileAndRefreshAsync error: {ex.Message}"); } 
				return Task.CompletedTask;		       
		    });
		}

        public void ScheduleStrategyUpdate(Account account, Instrument instrument, long strategyId, double pointValue, double tickSize)
        {
			_debouncer.Schedule(account, instrument, strategyId, pointValue, tickSize);
        }

		public void FlushAndClear(Account previousAccount, Instrument previousInstrument)
		{
		    try
		    {
		        if (previousAccount == null || previousInstrument == null) return;
				
				_debouncer.CancelAll();
				_positionStore.ClearStates();
				_fillsStore.Clear();
				
				_logger.Debug(
				    $"StrategySnapshotEngine.FlushAndClear: " +
				    $"Flushed states for {previousAccount.Name} | {previousInstrument.FullName}");

		        _orderBook.Clear();
		        _reconciliation.Clear();
				_attribution.Clear();
				_lastAggAveragePrice = 0.0;
				_lastAggDirection = MarketPosition.Flat;

		        var emptySnap = StrategySnapshot.Empty;
		        ResetAllHashesInternal();
		        foreach (var m in AllModes)
		        {
		            SetSnapshotInternal(m, emptySnap);
		            try { SnapshotChangedForMode?.Invoke(m, emptySnap); } catch { /* Ignore */ }
		        }
		        try { SnapshotChanged?.Invoke(emptySnap); } catch { /* Ignore */ }
		    }
		    catch (Exception ex) { _logger.Error($"StrategySnapshotEngine.FlushAndClear error: {ex.Message}"); } 
		}		
		
        #endregion
	    // ──────────────────────────────		
        #region Snapshot Building

		private Task RefreshSingleStrategyAndPublish(Account account, Instrument instrument, long strategyId, double pointValue, double tickSize)
		{
		    try
		    {
		        if (!ValidateAccountAndInstrument(account, instrument, pointValue, tickSize))
		        {
		            PublishEmptySnapshotIfNeeded();
		            return Task.CompletedTask;
		        }
				
				if (StrategyIdentity.IsManualStrategy(strategyId))
		        {
					if (_handlers.TryGetValue(StrategyOrigin.ManualStrategy, out var manualHandler))
					{
					    var manualAllowedIds = new HashSet<long> { StrategyIdentity.ManualStrategyId };

					    var refreshCtx = new StrategyRefreshOrdersContext
					    {
					        Account = account,
					        Instrument = instrument,
					        Orders = new ScopedOrdersAccess(_orderBook, manualAllowedIds, _logger),
					        LiveOrders = PositionMath.GetLiveOrdersForInstrument(account, instrument),
					        ManualPosition = GetManualPosition(account, instrument),
					        GetCategory = (o, pos) => _adapter.GetCategory(o, pos),
					        GetStrategyId = o => _adapter.GetStrategyId(o),
					        GetRawOwnerInfo = o => _adapter.GetRawOwnerInfo(o),
					        CreateDto = (sid, o, cat, raw) => CreateDto(sid, o, cat, raw),
					    };

					    manualHandler.RefreshOrders(refreshCtx);
					}

		            ComposeSnapshotAndPublishGated(account, instrument, pointValue, tickSize);
		            return Task.CompletedTask;
		        }
		
				var liveOrders = PositionMath.GetLiveOrdersForInstrument(account, instrument)
				    .Where(o => _adapter.CanHandle(o) && _adapter.GetStrategyId(o) == strategyId)
				    .ToList();

		        var entries = new List<StrategyOrderDto>();
		        var stops = new List<StrategyOrderDto>();
		        var targets = new List<StrategyOrderDto>();
				
				MarketPosition stratPosition = MarketPosition.Flat;
				if (_positionStore.TryGetState(strategyId, out var stratRt))
				    stratPosition = stratRt.Direction;				

		        foreach (var o in liveOrders)
		        {
		            var cat = _adapter.GetCategory(o, stratPosition);
		            var dto = CreateDto(_adapter.GetStrategyId(o), o, cat, _adapter.GetRawOwnerInfo(o));
		            if (dto == null) continue;
					
		            if ((cat & StrategyOrderCategory.Stop) != 0) 
						stops.Add(dto);
		            else if ((cat & StrategyOrderCategory.Target) != 0) 
						targets.Add(dto);
		            else 
						entries.Add(dto);
		        }

		        bool hasOrders = entries.Count > 0 || stops.Count > 0 || targets.Count > 0;
		        if (hasOrders)
		        {
		            string strategyName = liveOrders.Count > 0 
						? _adapter.GetStrategyName(liveOrders[0]) ?? string.Empty 
						: string.Empty;
		            _orderBook.Upsert(strategyId, new StrategyOrderBook(strategyId, strategyName, entries, stops, targets));
		        }		
		        else
		        {
		            _orderBook.Remove(strategyId);	
					
				    try
				    {
				        if (_positionStore.TryGetState(strategyId, out var rt) && (rt == null || (!rt.IsInitialized && rt.Quantity == 0)))
				        {
				            _positionStore.RemoveState(strategyId, account, instrument);
				            _logger.Debug($"StrategySnapshotEngine.RefreshSingleStrategyAndPublish: Removed position state for strategy {strategyId} (no orders)");
				        }
				    }
		            catch (Exception ex) { _logger.Error($"StrategySnapshotEngine.RefreshSingleStrategyAndPublish Remove position state error: {ex.Message}"); }
		        }
		
		        ComposeSnapshotAndPublishGated(account, instrument, pointValue, tickSize);
		    }
		    catch (Exception ex) { _logger.Error($"StrategySnapshotEngine.RefreshSingleStrategyAndPublish error: {ex.Message}"); }
		    return Task.CompletedTask;
		}
		
		private Task FullRebuildAndPublish(Account account, Instrument instrument, double pointValue, double tickSize)
		{
		    try
		    {
		        if (!ValidateAccountAndInstrument(account, instrument, pointValue, tickSize))
		        {
		            PublishEmptySnapshotIfNeeded();
		            return Task.CompletedTask;
		        }

		        _initialConsistencyCheckDone = true;

		        PublishEmptySnapshotIfNeeded();

		        _positionStore.TryRestore(account, instrument, _reconciliation.QueuedStrategyIds);

		        var orders = PositionMath.GetLiveOrdersForInstrument(account, instrument);
		        var builder = new Dictionary<long, StrategyOrderBook>();

		        var grouped = orders
		            .Where(o => _adapter.CanHandle(o))
		            .GroupBy(o => _adapter.GetStrategyId(o));

		        foreach (var g in grouped)
		        {
		            var id = g.Key;
		            var list = g.ToList();
		            var entries = new List<StrategyOrderDto>();
		            var stops = new List<StrategyOrderDto>();
		            var targets = new List<StrategyOrderDto>();

		            MarketPosition position = MarketPosition.Flat;
		            if (_positionStore.TryGetState(id, out var rt))
		                position = rt.Direction;

		            if (position == MarketPosition.Flat && id == StrategyIdentity.ManualStrategyId)
		            {
		                var accountPosition = account.Positions?.FirstOrDefault(p => p?.Instrument == instrument);
		                if (accountPosition != null)
		                    position = accountPosition.MarketPosition;
		            }

		            foreach (var o in list)
		            {
		                var cat = _adapter.GetCategory(o, position);
		                var dto = CreateDto(id, o, cat, _adapter.GetRawOwnerInfo(o));
		                if (dto == null) continue;
						
		                if ((cat & StrategyOrderCategory.Stop) != 0) 
							stops.Add(dto);
		                else if ((cat & StrategyOrderCategory.Target) != 0) 
							targets.Add(dto);
		                else 
							entries.Add(dto);
		            }

		            string name = id == StrategyIdentity.ManualStrategyId
		                ? StrategyIdentity.ManualStrategyName
		                : (list.Count > 0 ? _adapter.GetStrategyName(list[0]) ?? string.Empty : string.Empty);

		            _logger.Debug(
		                $"StrategySnapshotEngine.FullRebuildAndPublish: Add builder => Id={id} | " +
		                $"Entries={entries.Count} | Stops={stops.Count} | Targets={targets.Count}");

		            builder[id] = new StrategyOrderBook(id, name, entries, stops, targets);
		        }

		        _orderBook.ReplaceAll(builder);

		        var livePosition = BuildAccountNetPositionSnapshot(account, instrument);

		        int accountNet = livePosition.HasPosition
		            ? (livePosition.MarketPosition == MarketPosition.Long
		                ? livePosition.QuantityPositions
		                : -livePosition.QuantityPositions)
		            : 0;

		        _reconciliation.Run(account, instrument, accountNet);

		        if (IsSlotFullyFlat())
		        {
		            _awaitingInitialFlat = false;
		            ComposeSnapshotAndPublish(account, instrument, pointValue, tickSize);
		        }
		        else
		        {
		            _awaitingInitialFlat = true;
		            PublishEmptySnapshotIfNeeded();
		            _logger.Debug(
		                $"StrategySnapshotEngine.FullRebuildAndPublish: slot [{account?.Name}|{instrument?.FullName}] " +
		                "has open positions/orders at startup - withholding real snapshots until it reaches full flat.");
		        }
		    }
		    catch (Exception ex) { _logger.Error($"StrategySnapshotEngine.FullRebuildAndPublish error: {ex.Message}"); PublishEmptySnapshotIfNeeded(); }
		    return Task.CompletedTask;
		}

		private bool IsSlotFullyFlat()
		{
		    foreach (var kv in _orderBook.GetAll())
		    {
		        var ob = kv.Value;
		        if (ob != null && ((ob.Entries?.Count ?? 0) > 0 || (ob.Stops?.Count ?? 0) > 0 || (ob.Targets?.Count ?? 0) > 0))
		            return false;
		    }

		    foreach (var kv in _positionStore.GetStates())
		    {
		        var s = kv.Value;
		        if (s != null && s.IsInitialized && s.Quantity > 0)
		            return false;
		    }

		    return true;
		}

		private void ComposeSnapshotAndPublishGated(Account account, Instrument instrument, double pointValue, double tickSize)
		{
		    if (!_awaitingInitialFlat)
		    {
		        ComposeSnapshotAndPublish(account, instrument, pointValue, tickSize);
		        return;
		    }

		    if (IsSlotFullyFlat())
		    {
		        _awaitingInitialFlat = false;
		        _logger.Debug(
		            $"StrategySnapshotEngine.ComposeSnapshotAndPublishGated: " +
		            $"slot [{account?.Name}|{instrument?.FullName}] reached full flat - resuming normal snapshot publishing.");
		        ComposeSnapshotAndPublish(account, instrument, pointValue, tickSize);
		    }
		    else
		    {
		        PublishEmptySnapshotIfNeeded();
		    }
		}

		private void ComposeSnapshotAndPublish(Account account, Instrument instrument, double pointValue, double tickSize)
		{
		    try
		    {
		        var ctx = BuildSharedContext(account, instrument, tickSize);

		        foreach (var mode in AllModes)
		        {
		            var strategies = ComposeForMode(ctx, mode, pointValue, tickSize);

		            IReadOnlyDictionary<long, StrategyModel> strategiesReadOnly = new Dictionary<long, StrategyModel>(strategies);

		            long strategiesHash = HashStrategies(strategiesReadOnly);

		            if (GetLastHashInternal(mode) == strategiesHash) continue;
		            SetLastHashInternal(mode, strategiesHash);

		            long version = Interlocked.Increment(ref _snapshotVersion);
		            var snap = new StrategySnapshot(version, strategiesReadOnly);
		            SetSnapshotInternal(mode, snap);

		            try { SnapshotChangedForMode?.Invoke(mode, snap); }
		            catch (Exception ex) { _logger.Error($"StrategySnapshotEngine.ComposeSnapshotAndPublish SnapshotChangedForMode handler error: {ex.Message}"); }

		            if (mode == StrategyMode)
		            {
		                try { SnapshotChanged?.Invoke(snap); }
		                catch (Exception ex) { _logger.Error($"StrategySnapshotEngine.ComposeSnapshotAndPublish SnapshotChanged handler error: {ex.Message}"); }
		            }
		        }
		    }
		    catch (Exception ex) { _logger.Error($"StrategySnapshotEngine.ComposeSnapshotAndPublish error: {ex.Message}"); }
		}

		private SharedSnapshotContext BuildSharedContext(Account account, Instrument instrument, double tickSize)
		{
			var dict = _orderBook.GetAll();
			var positionStates = _positionStore.GetStates();
		    var keys = new HashSet<long>();

		    foreach (var k in dict.Keys)
		    {
		        if (k == long.MinValue) continue;
		        keys.Add(k);
		    }

		    foreach (var k in positionStates.Keys)
		    {
		        if (k == long.MinValue) continue;
		        keys.Add(k);
		    }

		    bool hasLong = false;
		    bool hasShort = false;
		    bool anyPosition = false;

		    foreach (var sId in keys)
		    {
		        positionStates.TryGetValue(sId, out var rs);
		        if (rs == null || !rs.IsInitialized || rs.Quantity <= 0) continue;

		        anyPosition = true;
		        if (rs.Direction == MarketPosition.Long) hasLong = true;
		        if (rs.Direction == MarketPosition.Short) hasShort = true;
		    }

		    _logger.Debug($"StrategySnapshotEngine.ComposeSnapshotAndPublish: HasLong={hasLong} | HasShort={hasShort} | AnyPos={anyPosition}");

		    var aggPos = BuildAccountNetPositionSnapshot(account, instrument);

			if (aggPos.HasPosition)
			{
				_lastAggAveragePrice = aggPos.AveragePrice;
				_lastAggDirection = aggPos.MarketPosition;
			}
			else if (_lastAggAveragePrice > 0.0 && hasLong && hasShort)
			{
				aggPos = new PositionSnapshot(_lastAggDirection, 0, _lastAggAveragePrice);
			}

		    var staticInfos = new Dictionary<long, StrategyStaticInfo>();
		    var aggregatedStrategyNames = new List<string>();
		    var aggregatedStrategyInfos = new List<AggregatedStrategyInfo>();

		    foreach (var sId in keys)
		    {
		        dict.TryGetValue(sId, out var local);
		        positionStates.TryGetValue(sId, out var positionState);

		        bool hasPosition = positionState != null && positionState.IsInitialized && positionState.Quantity > 0;
		        bool hasAnyOrders = local != null && (local.Entries.Count > 0 || local.Stops.Count > 0 || local.Targets.Count > 0);

		        if (!hasPosition && !hasAnyOrders) continue;
				if (!hasPosition && !StrategyIdentity.IsManualStrategy(sId) && _reconciliation.IsQueuedForCleanup(sId)) continue;

				var entries = local?.Entries ?? Array.Empty<StrategyOrderDto>();
				var stops = local?.Stops   ?? Array.Empty<StrategyOrderDto>();
				var targets = local?.Targets ?? Array.Empty<StrategyOrderDto>();
				var fills = _fillsStore.GetFills(sId);

				if (StrategyIdentity.IsManualStrategy(sId) && positionState != null)
				    _attribution.ReclassifyManualOrders(positionState, ref entries, ref stops, ref targets);

		        string name = StrategyIdentity.IsManualStrategy(sId)
		            ? StrategyIdentity.ManualStrategyName
		            : (!string.IsNullOrWhiteSpace(local?.Name)
		                ? local.Name
		                : (!string.IsNullOrWhiteSpace(positionState?.KnownName)
		                    ? positionState.KnownName
		                    : StrategyIdentity.UnknownStrategyName));

		        var ownPos = hasPosition
		            ? new PositionSnapshot(positionState.Direction, positionState.Quantity, positionState.AveragePrice)
		            : PositionSnapshot.Empty;

		        if (hasPosition)
		        {
		            aggregatedStrategyNames.Add(name);
		            aggregatedStrategyInfos.Add(new AggregatedStrategyInfo(sId, name, positionState.Direction, positionState?.FirstFillTimeUtc ?? DateTime.MinValue));     
		        }

		        var rawLevels = new Dictionary<long, List<StrategyOrderDto>>();

		        void AddToRawLevels(StrategyOrderDto order, double price)
		        {
		            if (order == null || double.IsNaN(price) || price <= 0) return;
		            long k = PriceToKey(price, tickSize);
		            if (!rawLevels.TryGetValue(k, out var list))
		            {
		                list = new List<StrategyOrderDto>();
		                rawLevels[k] = list;
		            }
		            list.Add(order);
		        }

		        foreach (var entry in entries)
		        {
		            if (entry == null) continue;
		            double entryPrice = entry.OrderType == OrderType.StopLimit
		                ? (entry.StopPrice > 0 ? entry.StopPrice : entry.LimitPrice)
		                : (entry.LimitPrice > 0 ? entry.LimitPrice : entry.StopPrice);

		            AddToRawLevels(entry, entryPrice);
		        }

		        foreach (var stop in stops)
		        {
		            if (stop == null) continue;
		            double stopPrice = stop.OrderType == OrderType.StopLimit
		                ? (stop.StopPrice > 0 ? stop.StopPrice : stop.LimitPrice)
		                : (stop.LimitPrice > 0 ? stop.LimitPrice : stop.StopPrice);

		            AddToRawLevels(stop, stopPrice);
		        }

		        foreach (var target in targets)
		        {
		            if (target == null) continue;
		            double targetPrice = target.OrderType == OrderType.StopLimit
		                ? (target.StopPrice > 0 ? target.StopPrice : target.LimitPrice)
		                : (target.LimitPrice > 0 ? target.LimitPrice : target.StopPrice);

		            AddToRawLevels(target, targetPrice);
		        }

		        staticInfos[sId] = new StrategyStaticInfo
		        {
		            Id = sId,
		            Name = name,
		            DisplayName = name,
		            Entries = entries,
		            Stops = stops,
		            Targets = targets,
		            Fills = fills,
		            HasPosition = hasPosition,
		            HasAnyOrders = hasAnyOrders,
		            OwnPos = ownPos,
		            Direction = positionState?.Direction ?? MarketPosition.Flat,
		            FirstFillTimeUtc = positionState?.FirstFillTimeUtc ?? DateTime.MinValue,
		            RawLevels = rawLevels
		        };
		    }

		    var orderedAggregatedStrategyInfos = aggregatedStrategyInfos
		        .OrderByDescending(a => a.FirstFillTimeUtc)
		        .ThenByDescending(a => a.Id)
		        .ToList();

		    return new SharedSnapshotContext
		    {
		        Keys = keys,
		        AggPos = aggPos,
		        StaticInfos = staticInfos,
		        AggregatedStrategyNames = aggregatedStrategyNames,
		        AggregatedStrategyInfos = orderedAggregatedStrategyInfos
		    };
		}

		private Dictionary<long, StrategyModel> ComposeForMode(SharedSnapshotContext ctx, StrategyCalculationMode mode, double pointValue, double tickSize)
		{
		    bool isAggregatedMode = GetModeFlags(mode);
		    var aggPos = ctx.AggPos;
		    var positionStates = _positionStore.GetStates();

		    var result = new Dictionary<long, StrategyModel>(ctx.StaticInfos.Count);

		    foreach (var sId in ctx.Keys)
		    {
		        if (!ctx.StaticInfos.TryGetValue(sId, out var info)) continue;

				bool useAggregatedSource = info.HasPosition && aggPos != null && isAggregatedMode;
				PositionSnapshot pos = !info.HasPosition ? PositionSnapshot.Empty : (useAggregatedSource ? aggPos : info.OwnPos);
		        var levels = BuildLevelsForStrategy(ctx, info, sId, pos, pointValue, tickSize, positionStates, isAggregatedMode);

		        result[sId] = new StrategyModel(
		            sId,
		            info.Name,
		            info.Entries, info.Stops, info.Targets,
		            levels,
		            pos,
		            info.Fills,
		            averageDisplayName: info.DisplayName,
		            isAggregated: isAggregatedMode, 
		            aggregatedStrategyNames: useAggregatedSource ? ctx.AggregatedStrategyNames : new List<string>(),
		            aggregatedStrategyInfos: useAggregatedSource ? ctx.AggregatedStrategyInfos : new List<AggregatedStrategyInfo>(),
		            levelsAveragePrice: pos.AveragePrice,
		            firstFillTimeUtc: info.FirstFillTimeUtc);
		    }

		    return result;
		}

		private List<StrategyPriceLevel> BuildLevelsForStrategy(SharedSnapshotContext ctx, StrategyStaticInfo info, long sId, 
			PositionSnapshot mPos, double pointValue, double tickSize, IReadOnlyDictionary<long, PositionState> positionStates, bool isAggregatedMode)
		{
		    var levels = new List<StrategyPriceLevel>();

		    positionStates.TryGetValue(sId, out var psForAmount);
		    bool stratHasPosition = psForAmount != null && psForAmount.IsInitialized && psForAmount.Quantity > 0;
		    bool stratIsLong = psForAmount?.Direction == MarketPosition.Long;
		    bool stratIsShort = psForAmount?.Direction == MarketPosition.Short;
		    double avgForAmount = mPos.AveragePrice;

			bool neverFilled = psForAmount == null || !psForAmount.IsInitialized;
			bool noPositionToProtect = mPos == null || !mPos.HasPosition;

		    foreach (var kvp in info.RawLevels.OrderBy(x => x.Key))
		    {
		        var orders = kvp.Value.Where(x => x != null).ToList();
		        if (orders.Count == 0) continue;

		        double price = KeyToPrice(kvp.Key, tickSize);

		        var entryOrders = orders.Where(o => (o.Category & StrategyOrderCategory.Entry) != 0).ToList();
		        var stopOrders = orders.Where(o => (o.Category & StrategyOrderCategory.Stop) != 0).ToList();
		        var targetOrders = orders.Where(o => (o.Category & StrategyOrderCategory.Target) != 0).ToList();

				if (neverFilled || noPositionToProtect)
				{
				    entryOrders = entryOrders.Concat(stopOrders).Concat(targetOrders).ToList();
				    stopOrders = new List<StrategyOrderDto>();
				    targetOrders = new List<StrategyOrderDto>();
				}

		        foreach (var grp in entryOrders
		            .GroupBy(o => (o.Action, o.OrderType))
		            .OrderBy(g => g.Key.Action.ToString())
		            .ThenBy(g => g.Key.OrderType.ToString()))
		        {
		            var grpList = grp.ToList();
		            int qty = grpList.Sum(o => Math.Abs(o.QuantityOrders));
		            if (qty > 0)
		                levels.Add(new StrategyPriceLevel(price, 0.0, qty, StrategyOrderCategory.Entry, grpList));
		        }

		        foreach (var grp in stopOrders
		            .GroupBy(o => (o.Action, o.OrderType))
		            .OrderBy(g => g.Key.Action.ToString())
		            .ThenBy(g => g.Key.OrderType.ToString()))
		        {
		            var grpList = grp.ToList();
		            int qty = grpList.Sum(o => Math.Abs(o.QuantityOrders));
		            if (qty <= 0) continue;
		            double amount = CalculateAmountForOrderGroup(price, avgForAmount, qty, pointValue, stratIsLong, stratIsShort);
		            levels.Add(new StrategyPriceLevel(price, amount, qty, StrategyOrderCategory.Stop, grpList));
		        }

		        foreach (var grp in targetOrders
		            .GroupBy(o => (o.Action, o.OrderType))
		            .OrderBy(g => g.Key.Action.ToString())
		            .ThenBy(g => g.Key.OrderType.ToString()))
		        {
		            var grpList = grp.ToList();
		            int qty = grpList.Sum(o => Math.Abs(o.QuantityOrders));
		            if (qty <= 0) continue;
		            double amount = CalculateAmountForOrderGroup(price, avgForAmount, qty, pointValue, stratIsLong, stratIsShort);
		            levels.Add(new StrategyPriceLevel(price, amount, qty, StrategyOrderCategory.Target, grpList));
		        }
		    }

		    return levels.OrderBy(x => x.PriceLevel).ToList();
		}

        private long HashStrategies(IReadOnlyDictionary<long, StrategyModel> strategies)
        {
            long h = 17L;
            foreach (var kv in strategies.OrderBy(x => x.Key))
            {
                var s = kv.Value;
                h = h * 31 + DeterministicHash(kv.Key.ToString());
                h = h * 31 + DeterministicHash(s?.Name ?? string.Empty);
                h = h * 31 + s.QuantityPositions.GetHashCode();
                h = h * 31 + s.AveragePrice.GetHashCode();
				h = h * 31 + s.LevelsAveragePrice.GetHashCode();
				h = h * 31 + s.IsAggregated.GetHashCode();
                h = h * 31 + (s.Entries?.Count ?? 0);
                h = h * 31 + (s.Stops?.Count ?? 0);
                h = h * 31 + (s.Targets?.Count ?? 0);
                h = h * 31 + (s.Levels?.Count ?? 0);

                if (s.Entries != null)
                    foreach (var o in s.Entries)
                    {
                        h = h * 31 + DeterministicHash(o?.OrderId ?? string.Empty);
                        h = h * 31 + o.LimitPrice.GetHashCode();
                        h = h * 31 + o.QuantityOrders.GetHashCode();
                    }

                if (s.Stops != null)
                    foreach (var o in s.Stops)
                    {
                        h = h * 31 + DeterministicHash(o?.OrderId ?? string.Empty);
                        h = h * 31 + o.StopPrice.GetHashCode();
                        h = h * 31 + o.QuantityOrders.GetHashCode();
                    }

                if (s.Targets != null)
                    foreach (var o in s.Targets)
                    {
                        h = h * 31 + DeterministicHash(o?.OrderId ?? string.Empty);
                        h = h * 31 + o.LimitPrice.GetHashCode();
                        h = h * 31 + o.QuantityOrders.GetHashCode();
                    }

                if (s.Levels != null)
                    foreach (var l in s.Levels)
                    {
                        h = h * 31 + Math.Round(l.PriceLevel, 8).GetHashCode();
                        h = h * 31 + Math.Round(l.Amount, 2).GetHashCode();
                        h = h * 31 + l.QuantityOrders.GetHashCode();
                        h = h * 31 + ((int)l.Kind).GetHashCode();

                        if (l.Orders != null)
                        {
                            foreach (var o in l.Orders)
                            {
                                h = h * 31 + DeterministicHash(o?.OrderId ?? string.Empty);
                                h = h * 31 + (o?.Category ?? StrategyOrderCategory.Unknown)
                                    .GetHashCode();
                                h = h * 31 + (o?.LimitPrice ?? 0.0).GetHashCode();
                                h = h * 31 + (o?.StopPrice ?? 0.0).GetHashCode();
                                h = h * 31 + (o?.QuantityOrders ?? 0).GetHashCode();
                            }
                        }
                    }

                if (s.Fills != null)
                {
                    h = h * 31 + s.Fills.Count.GetHashCode();
                    int start = Math.Max(0, s.Fills.Count - 10);
                    for (int i = start; i < s.Fills.Count; i++)
                    {
                        var f = s.Fills[i];
                        if (f == null) continue;
                        h = h * 31 + DeterministicHash(f.OrderId ?? string.Empty);
                        h = h * 31 + f.QuantityOrders.GetHashCode();
						h = h * 31 + Math.Round(f.Price, 8).GetHashCode();
                        h = h * 31 + f.TimeUtc.GetHashCode();
                    }
                }
            }
            return h;
        }

        private void PublishEmptySnapshotIfNeeded()
        {
            var cur = CurrentSnapshot;
            if (cur != null && cur.Strategies != null && cur.Strategies.Any())
            {
                ResetAllHashesInternal();
                var emptyDict = new Dictionary<long, StrategyModel>();
                foreach (var m in AllModes)
                {
                    long version = Interlocked.Increment(ref _snapshotVersion);
                    var s = new StrategySnapshot(version, emptyDict);
                    SetSnapshotInternal(m, s);
                    try { SnapshotChangedForMode?.Invoke(m, s); }
                    catch (Exception ex) { _logger.Error($"StrategySnapshotEngine.PublishEmptySnapshot handler error: {ex.Message}"); }

                    if (m == StrategyMode)
                    {
                        try { SnapshotChanged?.Invoke(s); }
                        catch (Exception ex) { _logger.Error($"StrategySnapshotEngine.PublishEmptySnapshot handler error: {ex.Message}"); }
                    }
                }
            } 
        }		

        #endregion		
	    // ──────────────────────────────		
        #region Execution Processing

        private void ProcessExecutionInternal(Account account, Instrument instrument, Execution execution, double pointValue, double tickSize)
        {
            try
            {
                if (account == null || instrument == null || execution == null) return;
                var order = execution.Order;
                if (order == null) return;

                _attribution.PruneCloseOrderBindings(account, instrument);
                _attribution.RecordCloseFillIfBound(order, Math.Abs(execution.Quantity));

                _logger.Debug(
                    $"StrategySnapshotEngine.ProcessExecutionInternal ProcessExecution: " +
                    $"Account={account.Name} | Instrument={instrument.FullName} | " +
                    $"OrderId={order.OrderId} | Qty={execution.Quantity} | Price={execution.Price}");

                long strategyId = _adapter.GetStrategyId(order);

                if (strategyId == StrategyIdentity.ManualStrategyId)
                    strategyId = _attribution.ResolveCloseExecutionTarget(order, execution, strategyId);

                var currentStates = _positionStore.GetStates();
                currentStates.TryGetValue(strategyId, out var existingState);

                var resolvedOrigin = _adapter.GetStrategyOrigin(order);

                if (existingState != null)
                {
                    bool originDemotion = resolvedOrigin == StrategyOrigin.ManualStrategy && 
						(existingState.Origin == StrategyOrigin.AtmStrategy || existingState.Origin == StrategyOrigin.NinjaScriptStrategy);     
                    if (originDemotion)
                        resolvedOrigin = existingState.Origin;
                }

                bool isManualExecution = strategyId == StrategyIdentity.ManualStrategyId;
                if (isManualExecution)
                    resolvedOrigin = StrategyOrigin.ManualStrategy;

                string rawOwnerInfo = _adapter.GetRawOwnerInfo(order);
                string strategyName = _adapter.GetStrategyName(order);

                _logger.Debug(
                    $"StrategySnapshotEngine.ProcessExecutionInternal Strategy execution: " +
                    $"StrategyId={strategyId} | Origin={resolvedOrigin} | " +
                    $"Action={order.OrderAction} | Qty={Math.Abs(execution.Quantity)} | Price={execution.Price}");

                if (isManualExecution)
                {
                    _executionStore.RecordExecution(
                        account.Name, instrument.FullName,
                        order.OrderAction, Math.Abs(execution.Quantity), execution.Price,
                        order.OrderId?.ToString() ?? string.Empty,
                        rawOwnerInfo ?? string.Empty,
                        isManual: true);
                }

                var allowedIds = new HashSet<long> { strategyId };
                var positionsAccess = new ScopedPositionsAccess( _positionStore, account, instrument, allowedIds, _logger);
                var ordersAccess = new ScopedOrdersAccess(_orderBook, allowedIds, _logger);
                var fillsAccess = new ScopedFillsAccess(_fillsStore, allowedIds, _logger);

                var ctx = new StrategyExecutionContext
                {
                    Account = account,
                    Instrument = instrument,
                    Execution = execution,
                    Order = order,
                    StrategyId = strategyId,
                    PointValue = pointValue,
                    TickSize = tickSize,
                    RawOwnerInfo = rawOwnerInfo,
                    StrategyName = strategyName,
                    Positions = positionsAccess,
                    Orders = ordersAccess,
                    Fills = fillsAccess,
                };

                if (_handlers.TryGetValue(resolvedOrigin, out var handler))
                {
                    handler.ApplyExecution(ctx);
                }
                else
                {
                    _logger.Debug(
                        $"StrategySnapshotEngine.ProcessExecutionInternal: " +
                        $"No handler for origin={resolvedOrigin} " + 
						$"StrategyId={strategyId} - execution dropped, quarantining any existing state. Register in BuildHandlers().");

                    if (existingState != null)
                        _reconciliation.MarkForCleanup(strategyId, $"orphaned, origin={resolvedOrigin}");
                }

                var afterStates = _positionStore.GetStates();
                bool handlerRemovedState = !afterStates.ContainsKey(strategyId) && strategyId != StrategyIdentity.ManualStrategyId;                 
                bool handlerClosedState = afterStates.TryGetValue(strategyId, out var afterState) && afterState?.LifecycleState == StrategyLifecycleState.Closed;
                   
                if (handlerRemovedState || handlerClosedState)
                    _reconciliation.MarkForCleanup(strategyId);

                ComposeSnapshotAndPublishGated(account, instrument, pointValue, tickSize);
            }
            catch (Exception ex) { _logger.Error($"StrategySnapshotEngine.ProcessExecutionInternal error: {ex.Message}"); }
        }			
				
        #endregion
	    // ──────────────────────────────
        #region Handlers wiring

		private Dictionary<StrategyOrigin, IStrategyTypeHandler> BuildHandlers()
		{
		    return new Dictionary<StrategyOrigin, IStrategyTypeHandler>
		    {
		        [StrategyOrigin.ManualStrategy] = new ManualStrategyHandler(_logger),
		        [StrategyOrigin.AtmStrategy] = new AtmStrategyHandler(_logger),
		        [StrategyOrigin.NinjaScriptStrategy] = new NinjaScriptStrategyHandler(_logger),
		    };
		}

		#endregion
	    // ──────────────────────────────		
        #region Helpers & Utilities

		private double CalculateAmountForOrderGroup(double orderPrice, double averagePrice, int quantity, double pointValue, bool isLong, bool isShort)
		{
		    if (quantity <= 0 || pointValue <= 0.0 || averagePrice <= 0.0) return 0.0; 

		    double perUnit = 0.0;		    
		    if (isLong)
		        perUnit = orderPrice - averagePrice;
		    else if (isShort)
		        perUnit = averagePrice - orderPrice;
		    
		    double totalAmount = perUnit * quantity * pointValue;
		    return Math.Round(totalAmount, 2, MidpointRounding.AwayFromZero);
		}		

		private PositionSnapshot BuildAccountNetPositionSnapshot(Account account, Instrument instrument)
		{
		    try { return PositionMath.BuildAccountNetPositionSnapshotStatic(account, instrument); }
		    catch (Exception ex) { _logger.Error($"StrategySnapshotEngine.BuildAccountNetPositionSnapshot error: {ex.Message}"); return PositionSnapshot.Empty; } 
		}
		
		private static bool GetModeFlags(StrategyCalculationMode mode)
		{
		    return mode == StrategyCalculationMode.Aggregated;
		}

        private bool ValidateAccountAndInstrument(Account account, Instrument instrument, double pointValue, double tickSize)
        {
            if (account == null || instrument == null || pointValue <= 0.0 || tickSize <= 0.0)
            {
                _logger.Debug($"StrategySnapshotEngine.ValidateAccountAndInstrument: Validation invalid");                   
                return false;
            }

            return true;
        }
				
		private MarketPosition GetManualPosition(Account account, Instrument instrument)
		{
		    MarketPosition pos = MarketPosition.Flat;
		    if (_positionStore.TryGetState(StrategyIdentity.ManualStrategyId, out var manualRt))
		        pos = manualRt.Direction;
		
		    if (pos == MarketPosition.Flat)
		    {
		        var accountPos = account.Positions?.FirstOrDefault(p => p?.Instrument == instrument);
		        if (accountPos != null) pos = accountPos.MarketPosition;
		    }
		    return pos;
		}		

        private StrategySnapshot GetSnapshotInternal(StrategyCalculationMode mode)
        {
            return _snapshotCache.Get(mode);
        }

        private void SetSnapshotInternal(StrategyCalculationMode mode, StrategySnapshot snap)
        {
            _snapshotCache.Set(mode, snap);
        }
		
		private long PriceToKey(double price, double tickSize)
		{
		    if (tickSize > 0.0)
		    {
		        double snappedPrice = PositionMath.SnapToTickStatic(price, tickSize);
		        return PositionMath.PriceToTicks(snappedPrice, tickSize);
		    }
		    return (long)Math.Round(price * 1_000_000.0, MidpointRounding.AwayFromZero);
		}

		private double KeyToPrice(long key, double tickSize)
		{
		    if (tickSize > 0.0) return PositionMath.TicksToPrice(key, tickSize);
		    return key / 1_000_000.0;
		}

        private long GetLastHashInternal(StrategyCalculationMode mode)
        {
            return _snapshotCache.GetHash(mode);
        }

        private void SetLastHashInternal(StrategyCalculationMode mode, long hash)
        {
            _snapshotCache.SetHash(mode, hash);
        }

        private static long DeterministicHash(string str)
        {
            if (string.IsNullOrEmpty(str)) return 0L;
            long hash = 17L;
            foreach (char c in str) hash = hash * 31L + c;
            return hash;
        }

        private void ResetAllHashesInternal()
        {
            _snapshotCache.ResetHashes();
        }
		
		private Task RunOnUiOrInline(Func<Task> body)
		{
		    return _runOnUiThreadAsync != null ? _runOnUiThreadAsync(body, DispatcherPriority.Normal) : body();       
		}

        #endregion
	    // ──────────────────────────────
        #region StrategyOrder Dtos
		
        private StrategyOrderDto CreateDto(long strategyId, Order o, StrategyOrderCategory category, string rawOwner)
        {
            if (o == null) return null;
			
		    double limit = double.IsNaN(o.LimitPrice) ? 0.0 : o.LimitPrice;
		    double stop = double.IsNaN(o.StopPrice) ? 0.0 : o.StopPrice;
		    string orderId = o.OrderId?.ToString() ?? string.Empty;
		    string name = o.Name ?? string.Empty;
			DateTime createdOrderTime = o.Time;
		    
		    return new StrategyOrderDto(
		        strategyId, 
		        orderId, 
		        name, 
		        o.Quantity, 
		        limit, 
		        stop, 
		        o.OrderState, 
		        category, 
		        rawOwner, 
		        _adapter.GetStrategyOrigin(o),
		        _adapter.GetOrderType(o),
				_adapter.GetOrderAction(o), 
			    createdOrderTime);			
        }

        #endregion
	    // ──────────────────────────────
        #region Disposal
		
		public void Dispose()
		{
			_debouncer.Dispose();		
			_orderBook.Clear();
			_positionStore.ClearStates();
			_fillsStore.Clear();
		    _reconciliation.Clear();
		    _lastAggAveragePrice = 0.0;
		    _lastAggDirection = MarketPosition.Flat;

		    foreach (var m in AllModes) SetSnapshotInternal(m, StrategySnapshot.Empty);
		    SnapshotChanged = null;
		    SnapshotChangedForMode = null;
		}

        #endregion
	    // ──────────────────────────────		
	    #region Nested Types

        private sealed class StrategyStaticInfo
        {
            public long Id;
            public string Name;
            public string DisplayName;
            public IReadOnlyList<StrategyOrderDto> Entries;
            public IReadOnlyList<StrategyOrderDto> Stops;
            public IReadOnlyList<StrategyOrderDto> Targets;
            public IReadOnlyList<StrategyFillDto> Fills;
            public bool HasPosition;
            public bool HasAnyOrders;
            public PositionSnapshot OwnPos; 
            public MarketPosition Direction;
            public DateTime FirstFillTimeUtc; 

            public Dictionary<long, List<StrategyOrderDto>> RawLevels;
        }

        private sealed class SharedSnapshotContext
        {
            public HashSet<long> Keys;
            public PositionSnapshot AggPos;
            public Dictionary<long, StrategyStaticInfo> StaticInfos;

            public List<string> AggregatedStrategyNames;
            public List<AggregatedStrategyInfo> AggregatedStrategyInfos;
        }

		#endregion
	    // ──────────────────────────────			
    }
	// ==============================
    #endregion
	// ==========================================================
    #region Strategies.Orchestration
	// ==============================
	#region StrategySlot

	internal sealed class StrategySlot : IDisposable
	{		
	    private static readonly TimeSpan RecomputeQuietPeriod = TimeSpan.FromMilliseconds(1500);
	    private static readonly TimeSpan EvictDelay = TimeSpan.FromMilliseconds(1000);

	    public SlotKey Key { get; }
	    public Account Account { get; }
	    public Instrument Instrument { get; }
	    public double PointValue { get; }
	    public double TickSize { get; }

	    private readonly StrategySnapshotEngine _engine;
	    private readonly StrategyExecutionStore _execStore;
	    private readonly StrategyAdapter _adapter;
	    private readonly IStrategyLogger _logger;

	    private CancellationTokenSource _recomputeCts = new CancellationTokenSource();
	    private readonly object _recomputeCtsLock = new object();

	    private volatile bool _disposed;
	
	    public StrategySnapshot CurrentSnapshot => _engine != null ? _engine.CurrentSnapshot : null;	       
	    public StrategySnapshot GetSnapshot(StrategyCalculationMode mode) => _engine != null ? _engine.GetSnapshot(mode) : null;		
	    public bool IsEmpty { get { var snap = CurrentSnapshot; return snap == null || snap.Strategies == null || snap.Strategies.Count == 0; } }

	    public event Action<StrategySlot, StrategyCalculationMode, StrategySnapshot> SnapshotChanged;
	    // ──────────────────────────────
	    #region Constructor
		
	    public StrategySlot(SlotKey key, Account account, Instrument instrument, 
			double pointValue, double tickSize, Func<Func<Task>, DispatcherPriority, Task> runOnUiThread, IStrategyLogger logger)
	    {
	        Key = key;
	        Account = account;
	        Instrument = instrument;
	        PointValue = pointValue;
	        TickSize = tickSize;
	        _logger = logger ?? NullLogger.Instance;
	        _execStore = new StrategyExecutionStore(_logger);
	        _adapter = new StrategyAdapter();
	        _engine = new StrategySnapshotEngine(key, _adapter, _execStore, runOnUiThread, _logger);
	        _engine.SnapshotChangedForMode += OnStateSnapshotChanged;
	    }
		
	    #endregion
	    // ──────────────────────────────
	    #region Event forwarding			

		private void OnStateSnapshotChanged(StrategyCalculationMode mode, StrategySnapshot snapshot)
		{
		    if (_disposed) return;

		    var handler = SnapshotChanged;
		    if (handler != null) handler(this, mode, snapshot);
		}
		
	    #endregion
	    // ──────────────────────────────
	    #region Public API		

	    public IReadOnlyDictionary<long, PositionState> GetPositionStatesSnapshot() => _engine.GetPositionStatesSnapshot();
	       
	    public long GetStrategyId(Order order) => _engine.GetStrategyId(order);
	       				
	    public Task BuildFullSnapshotAsync() => _engine.BuildFullSnapshotAsync(Account, Instrument, PointValue, TickSize);
	       
	    public Task ReconcileAndRefreshAsync(int accountNet) => _engine.ReconcileAndRefreshAsync(Account, Instrument, PointValue, TickSize, accountNet);
	       
	    public void ScheduleStrategyUpdate(long strategyId) => _engine.ScheduleStrategyUpdate(Account, Instrument, strategyId, PointValue, TickSize);
	       
	    public void ProcessExecution(Execution exec) => _engine.ProcessExecution(Account, Instrument, exec, PointValue, TickSize);
	       
		public void UpdateSignalNames(string stopNames, string targetNames) => _adapter.UpdateSignalNames(stopNames, targetNames);		   

	    public void FlushAndClear() => _engine.FlushAndClear(Account, Instrument);
	       
	    #endregion
	    // ──────────────────────────────
	    #region Deferred actions

		public void HandleOrderUpdate(Order order, int accountNet)
		{
		    if (_disposed || order == null) return;
		
		    long strategyId = _engine.GetStrategyId(order);
		    var owner = order.GetOwnerStrategy();
		    bool isTerminal = Order.IsTerminalState(order.OrderState);		
		    bool isOwnerless = owner == null;
		
		    if (isTerminal)
		        ScheduleStrategyUpdate(strategyId);

		    if (isOwnerless)
		    {
		        if (order.OrderState != OrderState.Initialized && !isTerminal)
		        {
		            if (_engine.IsSimpleOwnerlessOrder(strategyId))
		                ScheduleStrategyUpdate(strategyId);
		            else
		                ScheduleQuiescentRecompute("Ownerless live", accountNet);
		        }
		
		        if (isTerminal)
		        {				
		            ScheduleQuiescentRecompute("Ownerless terminal", accountNet);
		            var orderId = order.OrderId?.ToString() ?? string.Empty;
		            ScheduleDelayedEvict("EvictOwnerlessOrder", orderId);
		        }
		    }
		    else if (isTerminal)
		    {
		        ScheduleQuiescentRecompute("ATM terminal", accountNet); 
		        var orderId = order.OrderId?.ToString() ?? string.Empty;
		        ScheduleDelayedEvict("EvictOrder", orderId);
		    }
		    else
		    {
		        ScheduleStrategyUpdate(strategyId);
		    }
		}

		public void ScheduleQuiescentRecompute(string reason, int accountNet)
		{
		    if (_disposed) return;
		
		    CancellationTokenSource newCts;
		    CancellationTokenSource oldCts;
		    CancellationToken token;
		
		    lock (_recomputeCtsLock)
		    {
		        oldCts = _recomputeCts;
		        newCts = new CancellationTokenSource();
		        _recomputeCts = newCts;
		        token = newCts.Token;
		    }
		
		    try { oldCts.Cancel(); } catch { /* ignore */ }
		    try { oldCts.Dispose(); } catch { /* ignore */ }
		
		    var capturedNet = accountNet;
		    var self = this;
		
		    Task.Run(async () =>
		    {
		        try
		        {
		            await Task.Delay(RecomputeQuietPeriod, token).ConfigureAwait(false);
		            if (token.IsCancellationRequested) return;
		
		            lock (self._recomputeCtsLock) 
					{ 
						if (!ReferenceEquals(self._recomputeCts, newCts)) return; 
					}

		            await self.ReconcileAndRefreshAsync(capturedNet).ConfigureAwait(false);
		        }
		        catch (OperationCanceledException) { /* superseded by a newer event */ }
		        catch (Exception ex) { self._logger.Error($"StrategySlot [{self.Key}] ScheduleQuiescentRecompute ({reason}) error: {ex.Message}"); }
		        finally
		        {
		            lock (self._recomputeCtsLock)
		            {
		                if (ReferenceEquals(self._recomputeCts, newCts))
		                {
		                    try { newCts.Dispose(); } catch { /* ignore */ }
		                }
		            }
		        }
		    });
		}

	    private void ScheduleDelayedEvict(string reason, string orderId)
	    {
	        if (_disposed || string.IsNullOrEmpty(orderId)) return;
	
	        var self = this;
	        Task.Run(async () =>
	        {
	            try
	            {
	                await Task.Delay(EvictDelay).ConfigureAwait(false);
	                if (!self._disposed)
	                    self._adapter.EvictOrderFromCache(orderId);
	            }
	            catch (Exception ex) { self._logger.Error($"StrategySlot [{self.Key}] ScheduleDelayedEvict ({reason}) error: {ex.Message}"); }
	        });
	    }
		
	    #endregion
	    // ──────────────────────────────
	    #region Dispose

	    public void Dispose()
	    {
	        if (_disposed) return;
	        _disposed = true;

	        lock (_recomputeCtsLock)
	        {
	            try { _recomputeCts.Cancel(); } catch { /* ignore */ }
	            try { _recomputeCts.Dispose(); } catch { /* ignore */ }
	        }

	        if (_engine != null)
	        {
	            _engine.SnapshotChangedForMode -= OnStateSnapshotChanged;
	            try { _engine.FlushAndClear(Account, Instrument); } catch { /* ignore */ }
	            try { _engine.Dispose(); } catch { /* ignore */ }
	        }

	        try { if (_execStore != null) _execStore.Dispose(); } catch { /* ignore */ }
	    }
		
	    #endregion
	    // ──────────────────────────────		
	}

	#endregion	
	// ==============================
	#region StrategyRegistry

	internal sealed class StrategyRegistry : IDisposable
	{
	    private readonly Func<Func<Task>, DispatcherPriority, Task> _runOnUiThread;
	    private readonly IStrategyLogger _logger;

	    private readonly Dictionary<SlotKey, StrategySlot> _slots = new Dictionary<SlotKey, StrategySlot>();
	    private readonly object _slotsLock = new object();

	    private readonly HashSet<string> _subscribedAccounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
	    private readonly object _subscriptionLock = new object();

	    private DateTime _lastSlotCleanupUtc = DateTime.UtcNow;
	    private static readonly TimeSpan SlotCleanupInterval = TimeSpan.FromMinutes(5);

	    private readonly Dictionary<SlotKey, int> _accountNetCache = new Dictionary<SlotKey, int>();
	    private readonly object _accountNetLock = new object();

	    private string _nsStopNames = "Stop";
	    private string _nsTargetNames = "Target";
	    private readonly object _signalNamesLock = new object();

	    private volatile bool _disposed;

	    public bool IsEmpty { get { lock (_slotsLock) { return _slots.Count == 0; } } }

	    public event Action<StrategyCalculationMode, MergedStrategySnapshot> MergedSnapshotChanged;
	    // ──────────────────────────────
	    #region Constructor
		
	    private StrategyRegistry(Func<Func<Task>, DispatcherPriority, Task> runOnUiThread, IStrategyLogger logger)
	    {
	        _runOnUiThread = runOnUiThread;
	        _logger = logger ?? NullLogger.Instance;
	    }
		
	    #endregion
	    // ──────────────────────────────
	    #region Process-wide shared instance (ref-counted)

	    private static readonly object _sharedLock = new object();
	    private static StrategyRegistry _shared;
	    private static int _refCount;
	    private static readonly TimeSpan CacheClearDelay = TimeSpan.FromSeconds(2);
	    private static CancellationTokenSource _pendingTeardownCts;

	    public static StrategyRegistry Acquire(Func<Func<Task>, DispatcherPriority, Task> runOnUiThread, IStrategyLogger logger = null)
	    {
	        MemCacheLifecycleGuard.EnsureRegistered(logger);

	        lock (_sharedLock)
	        {
	            if (_pendingTeardownCts != null)
	            {
	                try { _pendingTeardownCts.Cancel(); } catch { /* ignore */ }
	                try { _pendingTeardownCts.Dispose(); } catch { /* ignore */ }
	                _pendingTeardownCts = null;
	            }

	            if (_shared == null)
	                _shared = new StrategyRegistry(runOnUiThread, logger);

	            _refCount++;
	            return _shared;
	        }
	    }

	    public static void Release(StrategyRegistry instance)
	    {
	        CancellationTokenSource cts;
	        StrategyRegistry instanceToTearDown;
	        IStrategyLogger logger;

	        lock (_sharedLock)
	        {
	            if (instance == null || instance != _shared) return;

	            _refCount--;
	            if (_refCount > 0) return;

	            logger = instance._logger;
	            instanceToTearDown = _shared;

	            cts = new CancellationTokenSource();
	            _pendingTeardownCts = cts;
	        }

	        var token = cts.Token;

	        _ = Task.Run(async () =>
	        {
	            try
	            {
	                await Task.Delay(CacheClearDelay, token).ConfigureAwait(false);
	                if (token.IsCancellationRequested) return;

	                lock (_sharedLock)
	                {
	                    if (!ReferenceEquals(_pendingTeardownCts, cts)) return;
	                    if (_refCount > 0) return;

	                    _pendingTeardownCts = null;
	                    _shared = null;
	                    _refCount = 0;
	                }

	                try { instanceToTearDown.Dispose(); } catch { /* ignore */ }
					logger?.Debug(
						$"StrategyRegistry.Release: " +  
						$"no StrategyIdentifier indicator left on the platform " + 
						$"for {CacheClearDelay.TotalSeconds} seconds - slot registry disposed.");

	                MemCacheLifecycleGuard.ClearNow(logger, $"last StrategyIdentifier indicator released - {CacheClearDelay.TotalSeconds} seconds elapsed");	                   
	            }
	            catch (TaskCanceledException) { /* reclaimed by a new Acquire() - expected */ }
	            catch (Exception ex) { logger?.Error($"StrategyRegistry.Release delayed teardown error: {ex.Message}"); }   
	        });
	    }

	    #endregion
	    // ──────────────────────────────
	    #region Mem-Cache Lifecycle (independent of strategy/slot cleanup)

	    private static class MemCacheLifecycleGuard
	    {
	        private static readonly object _guardLock = new object();
	        private static bool _hooksRegistered;

	        public static void EnsureRegistered(IStrategyLogger logger)
	        {
	            if (_hooksRegistered) return;

	            lock (_guardLock)
	            {
	                if (_hooksRegistered) return;
	                _hooksRegistered = true;

	                try { AppDomain.CurrentDomain.ProcessExit += (s, e) => ClearNow(logger, "ProcessExit - platform closed"); }
	                catch { /* best effort - never let hook registration break Acquire() */ }

	                try { AppDomain.CurrentDomain.DomainUnload += (s, e) => ClearNow(logger, "DomainUnload - scripts recompiled"); }
	                catch { /* best effort */ }
	            }
	        }

	        public static void ClearNow(IStrategyLogger logger, string reason)
	        {
	            try
	            {
	                StrategyCacheManager.ClearAll();
	                logger?.Debug($"StrategyRegistry.MemCacheLifecycleGuard: mem-cache cleared ({reason}).");
	            }
	            catch { /* best-effort - never throw out of a shutdown/unload event handler */ }
	        }
	    }

	    #endregion
	    // ──────────────────────────────
	    #region AccountNet

	    public void UpdateAccountNet(SlotKey key, int accountNet)
	    {
	        lock (_accountNetLock) { _accountNetCache[key] = accountNet; }
	    }

	    private int GetAccountNet(SlotKey key)
	    {
	        lock (_accountNetLock)
	        {
	            int net;
	            return _accountNetCache.TryGetValue(key, out net) ? net : 0;
	        }
	    }

	    #endregion
	    // ──────────────────────────────
	    #region Signal Names

		public void UpdateSignalNamesForAll(string stopNames, string targetNames)
		{
		    lock (_signalNamesLock)
		    {
		        _nsStopNames = stopNames ?? "Stop";
		        _nsTargetNames = targetNames ?? "Target";
		    }
		
		    List<StrategySlot> slots;
		    lock (_slotsLock) { slots = new List<StrategySlot>(_slots.Values); }
		
		    foreach (var slot in slots)
		    {
		        try { slot.UpdateSignalNames(stopNames, targetNames); } catch { /* ignore */ }		       
		    }
		}

	    private void GetCurrentSignalNames(out string stop, out string target)
	    {
	        lock (_signalNamesLock)
	        {
	            stop = _nsStopNames;
	            target = _nsTargetNames;
	        }
	    }

	    #endregion
	    // ──────────────────────────────
	    #region Position States (for debug / hover logic)

	    public IReadOnlyDictionary<long, PositionState> GetPositionStatesSnapshot(Account account, Instrument instrument)
	    {
	        if (account == null || instrument == null) return null;
	        var slot = GetSlot(new SlotKey(account, instrument));
	        return slot?.GetPositionStatesSnapshot();
	    }

	    #endregion
	    // ──────────────────────────────
	    #region Account subscriptions

	    private void EnsureAccountSubscribed(Account account)
	    {
	        if (account == null) return;

	        lock (_subscriptionLock)
	        {
	            if (_subscribedAccounts.Contains(account.Name)) return;
	            _subscribedAccounts.Add(account.Name);
	        }

	        try { account.OrderUpdate += OnAccountOrderUpdate; } catch { /* ignore */ }
	        try { account.ExecutionUpdate += OnAccountExecutionUpdate; } catch { /* ignore */ }
	        try { account.PositionUpdate += OnAccountPositionUpdate; } catch { /* ignore */ }

	        _logger.Debug($"StrategyRegistry.EnsureAccountSubscribed: Subscribed to account [{account.Name}]");
	    }
		
	    #endregion		
	    // ──────────────────────────────
	    #region Account event handlers

	    private void OnAccountOrderUpdate(object sender, OrderEventArgs e)
	    {
	        if (_disposed || e?.Order == null || e.Order.Instrument == null) return;

	        try
	        {
	            var order = e.Order;
	            var account = sender as Account;
	            if (account == null) return;

	            HandleOrderUpdate(account, order);
	        }
	        catch (Exception ex) { _logger.Error($"StrategyRegistry.OnAccountOrderUpdate error: {ex.Message}"); }
	    }

	    private void OnAccountExecutionUpdate(object sender, ExecutionEventArgs e)
	    {
	        if (_disposed || e?.Execution == null || e.Execution.Instrument == null) return;

	        try
	        {
	            var account = sender as Account;
	            if (account == null) return;

	            HandleExecutionUpdate(account, e.Execution);
	        }
	        catch (Exception ex) { _logger.Error($"StrategyRegistry.OnAccountExecutionUpdate error: {ex.Message}"); }
	    }

	    private void OnAccountPositionUpdate(object sender, PositionEventArgs e)
	    {
	        if (_disposed || e?.Position == null || e.Position.Instrument == null) return;

	        try
	        {
	            var account = sender as Account;
	            if (account == null) return;

	            HandlePositionUpdate(account, e.Position);
	        }
	        catch (Exception ex) { _logger.Error($"StrategyRegistry.OnAccountPositionUpdate error: {ex.Message}"); }
	    }

	    #endregion
	    // ──────────────────────────────
	    #region Slot Management

	    public async Task EnsureSlotExistsAsync(Account account, Instrument instrument)
	    {
	        if (_disposed || account == null || instrument == null) return;

	        double pointValue = instrument.MasterInstrument != null ? instrument.MasterInstrument.PointValue : 0.0;	           
	        double tickSize = instrument.MasterInstrument != null ? instrument.MasterInstrument.TickSize : 0.0;
	           
	        if (pointValue <= 0.0 || tickSize <= 0.0) return;

	        var key = new SlotKey(account, instrument);

	        lock (_slotsLock) { if (_slots.ContainsKey(key)) return; }

	        var newSlot = new StrategySlot(key, account, instrument, pointValue, tickSize, _runOnUiThread, _logger);

	        string entry, stop, target;
	        GetCurrentSignalNames(out stop, out target);
	        newSlot.UpdateSignalNames(stop, target);

	        bool added = false;

	        lock (_slotsLock)
	        {
	            if (_slots.ContainsKey(key))
	            {
	                try { newSlot.Dispose(); } catch { /* ignore */ }
	                newSlot = null;
	            }
	            else
	            {
	                newSlot.SnapshotChanged += OnSlotSnapshotChanged;
	                _slots[key] = newSlot;
	                added = true;
	            }
	        }

	        EnsureAccountSubscribed(account);

	        if (added && newSlot != null)
	        {
	            _logger.Debug($"StrategyRegistry.EnsureSlotExistsAsync: Slot created [{key}]");
	            try { await newSlot.BuildFullSnapshotAsync().ConfigureAwait(false); }
	            catch (Exception ex) { _logger.Error($"StrategyRegistry.EnsureSlotExistsAsync: BuildFullSnapshotAsync failed for [{key}]: {ex.Message}"); }
	        }
	    }

	    private StrategySlot GetSlot(SlotKey key)
	    {
	        StrategySlot slot;
	        lock (_slotsLock) { _slots.TryGetValue(key, out slot); }
	        return slot;
	    }

	    #endregion
	    // ──────────────────────────────
	    #region Slot Cleanup (event-driven, throttled by time)

	    private void TryPeriodicSlotCleanup()
	    {
	        if (DateTime.UtcNow - _lastSlotCleanupUtc < SlotCleanupInterval) return;
	        _lastSlotCleanupUtc = DateTime.UtcNow;

	        List<SlotKey> toRemove;
	        lock (_slotsLock)
	        {
				toRemove = _slots
				    .Where(kv => kv.Value.IsEmpty)
				    .Select(kv => kv.Key)
				    .ToList();
	        }

	        foreach (var key in toRemove)
	        {
	            StrategySlot slot;
	            lock (_slotsLock)
	            {
	                if (!_slots.TryGetValue(key, out slot)) continue;
	                if (!slot.IsEmpty) continue;
	                _slots.Remove(key);
	            }

	            slot.SnapshotChanged -= OnSlotSnapshotChanged;
	            try { slot.Dispose(); } catch { /* ignore */ }

	            lock (_accountNetLock) { _accountNetCache.Remove(key); }

	            try
	            {
	                var handler = MergedSnapshotChanged;
	                if (handler != null)
	                {
	                    foreach (var mode in new[] { StrategyCalculationMode.Personal, StrategyCalculationMode.Aggregated })
	                    {
	                        var emptyMerged = new MergedStrategySnapshot(
	                            DateTime.UtcNow.Ticks,
	                            new Dictionary<CompositeStrategyKey, StrategyModel>(),
	                            new List<SlotReport> { new SlotReport { Key = key, StrategyCount = 0, Snapshot = null } });
	                        handler(mode, emptyMerged);
	                    }
	                }
	            }
	            catch { /* ignore */ }

	            _logger.Debug($"StrategyRegistry.EnsureSlotExistsAsync: Slot removed (empty after cleanup) [{key}]");
	        }
	    }

	    #endregion
	    // ──────────────────────────────
	    #region Start Scan

	    public async Task ScanAllAccountsAsync()
	    {
	        if (_disposed) return;

	        var found = new Dictionary<SlotKey, Tuple<Account, Instrument>>();

	        try
	        {
	            lock (StrategyBase.All)
	            {
	                foreach (StrategyBase s in StrategyBase.All)
	                {
	                    try
	                    {
	                        if (s == null || s.Account == null || s.Instrument == null) continue;
	                        if (s.State != State.Realtime) continue;

	                        var k = new SlotKey(s.Account, s.Instrument);
	                        if (!found.ContainsKey(k))
	                            found[k] = Tuple.Create(s.Account, s.Instrument);
	                    }
	                    catch { /* ignore */ }
	                }
	            }
	        }
	        catch (Exception ex) { _logger.Error($"StrategyRegistry.ScanAllAccountsAsync: StrategyBase scan error: {ex.Message}"); }

	        try
	        {
	            lock (Account.All)
	            {
	                foreach (Account account in Account.All)
	                {
	                    try
	                    {
	                        if (account == null) continue;
	                        if (account.ConnectionStatus != ConnectionStatus.Connected) continue;
	                        if (account.Orders == null) continue;

	                        foreach (var order in account.Orders)
	                        {
	                            try
	                            {
	                                if (order == null || order.Instrument == null) continue;
	                                if (Order.IsTerminalState(order.OrderState)) continue;

	                                var k = new SlotKey(account, order.Instrument);
	                                if (!found.ContainsKey(k))
	                                    found[k] = Tuple.Create(account, order.Instrument);
	                            }
	                            catch { /* ignore */ }
	                        }
	                    }
	                    catch { /* ignore */ }
	                }
	            }
	        }
	        catch (Exception ex) { _logger.Error($"StrategyRegistry.ScanAllAccountsAsync: Orders scan error: {ex.Message}"); }

	        try
	        {
	            lock (Account.All)
	            {
	                foreach (Account account in Account.All)
	                {
	                    try
	                    {
	                        if (account == null) continue;
	                        if (account.ConnectionStatus != ConnectionStatus.Connected) continue;
	                        if (account.Positions == null) continue;

	                        foreach (var position in account.Positions)
	                        {
	                            try
	                            {
	                                if (position == null || position.Instrument == null) continue;
	                                if (position.MarketPosition == MarketPosition.Flat) continue;
	                                if (position.Quantity == 0) continue;

	                                var k = new SlotKey(account, position.Instrument);
	                                if (!found.ContainsKey(k))
	                                    found[k] = Tuple.Create(account, position.Instrument);
	                            }
	                            catch { /* ignore */ }
	                        }
	                    }
	                    catch { /* ignore */ }
	                }
	            }
	        }
	        catch (Exception ex) { _logger.Error($"StrategyRegistry.ScanAllAccountsAsync: Positions scan error: {ex.Message}"); }

	        _logger.Debug($"StrategyRegistry.ScanAllAccountsAsync: Found {found.Count.ToString()} active pair(s)");

	        foreach (var kv in found)
	        {
	            var account = kv.Value.Item1;
	            var instrument = kv.Value.Item2;
	            try { await EnsureSlotExistsAsync(account, instrument).ConfigureAwait(false); }
	            catch (Exception ex)
	            {
	                _logger.Error(
						$"StrategyRegistry.ScanAllAccountsAsync: " + 
						$"EnsureSlotExistsAsync error for [{account.Name}] × [{instrument.FullName}]: {ex.Message}");	                   
	            }
	        }

	        _logger.Debug($"StrategyRegistry.ScanAllAccountsAsync: Registry now has {_slots.Count.ToString()} slot(s)");
	    }

	    #endregion
	    // ──────────────────────────────
	    #region Event Routing

	    public void HandleOrderUpdate(Account account, Order order)
	    {
	        if (_disposed || account == null || order == null || order.Instrument == null) return;

	        var key = new SlotKey(account.Name, order.Instrument.FullName);
	        var slot = GetSlot(key);

	        if (slot == null)
	        {
	            var capturedAccount = account;
	            var capturedInstrument = order.Instrument;
	            Task.Run(() => EnsureSlotExistsAsync(capturedAccount, capturedInstrument));
	            return;
	        }

	        int accountNet = GetAccountNet(key);
	        slot.HandleOrderUpdate(order, accountNet);
	    }

	    public void HandleExecutionUpdate(Account account, Execution exec)
	    {
	        if (_disposed || account == null || exec == null || exec.Instrument == null) return;

	        var key = new SlotKey(account.Name, exec.Instrument.FullName);
	        var slot = GetSlot(key);

	        if (slot == null) return;

	        slot.ProcessExecution(exec);
	    }

		public void HandlePositionUpdate(Account account, Position position)
		{
		    if (_disposed || account == null || position == null || position.Instrument == null) return;
		
		    var key = new SlotKey(account.Name, position.Instrument.FullName);
		    var slot = GetSlot(key);
		
		    if (slot == null)
		    {
		        if (position.MarketPosition != MarketPosition.Flat && position.Quantity > 0)
		        {
		            var capturedAccount = account;
		            var capturedInstrument = position.Instrument;
		            Task.Run(() => EnsureSlotExistsAsync(capturedAccount, capturedInstrument));
		        }
		        return;
		    }

		    int accountNet = ComputeAccountNet(account, position.Instrument);
		    UpdateAccountNet(key, accountNet);

		    slot.ScheduleQuiescentRecompute("PositionUpdate", accountNet);
		}

	    private static int ComputeAccountNet(Account account, Instrument instrument)
	    {
	        try
	        {
	            if (account?.Positions == null) return 0;

	            int net = 0;
	            foreach (var p in account.Positions)
	            {
	                if (p == null || p.Instrument == null) continue;
	                if (!string.Equals(p.Instrument.FullName, instrument.FullName, StringComparison.OrdinalIgnoreCase)) continue;	                       

	                if (p.MarketPosition == MarketPosition.Long) net += p.Quantity;
	                else if (p.MarketPosition == MarketPosition.Short) net -= p.Quantity;
	            }
	            return net;
	        }
	        catch { return 0; }
	    }

	    #endregion
	    // ──────────────────────────────
	    #region Merged Snapshot

	    public MergedStrategySnapshot GetMergedSnapshot(Account account, Instrument instrument, StrategyCalculationMode mode)
	    {
	        if (account == null || instrument == null) return MergedStrategySnapshot.Empty;

	        var key = new SlotKey(account, instrument);
	        var slot = GetSlot(key);

	        if (slot == null) return MergedStrategySnapshot.Empty;

	        return BuildMergedSnapshot(slot, mode);
	    }

	    private void OnSlotSnapshotChanged(StrategySlot slot, StrategyCalculationMode mode, StrategySnapshot snapshot)
	    {
	        if (_disposed) return;

	        TryPeriodicSlotCleanup();

	        var merged = BuildMergedSnapshot(slot, mode);
	        var handler = MergedSnapshotChanged;
	        if (handler != null) handler(mode, merged);
	    }

		private MergedStrategySnapshot BuildMergedSnapshot(StrategySlot slot, StrategyCalculationMode mode)
		{
		    var snapshot  = slot.GetSnapshot(mode);
		    var strategies = new Dictionary<CompositeStrategyKey, StrategyModel>();
		    var reports = new List<SlotReport>(1);
		
		    reports.Add(new SlotReport
		    {
		        Key = slot.Key,
		        StrategyCount = snapshot?.Strategies?.Count ?? 0,
		        Snapshot = snapshot
		    });
		
		    if (snapshot?.Strategies != null)
		    {
		        foreach (var kv in snapshot.Strategies)
		        {
		            var compositeKey = new CompositeStrategyKey(slot.Key, kv.Key);
		            strategies[compositeKey] = kv.Value;
		        }
		    }
		
		    return new MergedStrategySnapshot(DateTime.UtcNow.Ticks, strategies, reports);
		}

	    #endregion
	    // ──────────────────────────────
	    #region Dispose

	    public void Dispose()
	    {
	        if (_disposed) return;
	        _disposed = true;

	        List<string> accountNames;
	        lock (_subscriptionLock)
	        {
	            accountNames = new List<string>(_subscribedAccounts);
	            _subscribedAccounts.Clear();
	        }

	        lock (Account.All)
	        {
	            foreach (var name in accountNames)
	            {
	                foreach (Account acc in Account.All)
	                {
	                    if (acc != null && string.Equals(acc.Name, name, StringComparison.OrdinalIgnoreCase))
	                    {
	                        try { acc.OrderUpdate -= OnAccountOrderUpdate; } catch { /* ignore */ }
	                        try { acc.ExecutionUpdate -= OnAccountExecutionUpdate; } catch { /* ignore */ }
	                        try { acc.PositionUpdate -= OnAccountPositionUpdate; } catch { /* ignore */ }
	                    }
	                }
	            }
	        }

	        List<StrategySlot> toDispose;
	        lock (_slotsLock)
	        {
	            toDispose = new List<StrategySlot>(_slots.Values);
	            _slots.Clear();
	        }

	        foreach (var slot in toDispose)
	        {
	            slot.SnapshotChanged -= OnSlotSnapshotChanged;
	            try { slot.Dispose(); } catch { /* ignore */ }
	        }
	    }

	    #endregion
	    // ──────────────────────────────		
	}

	#endregion	
	// ==============================
    #endregion
	// ==========================================================
    #region Strategies.Utilities
	// ==============================
    #region StrategyDebouncer

    internal sealed class StrategyDebouncer : IDisposable
    {
        private readonly int _delayMs;
        private readonly Func<Account, Instrument, long, double, double, Task> _onFired;
        private readonly Func<Func<Task>, DispatcherPriority, Task> _runOnUiThreadAsync;
        private readonly IStrategyLogger _logger;
 
        private readonly object _lock = new object();
        private readonly Dictionary<long, CancellationTokenSource> _pending = new Dictionary<long, CancellationTokenSource>();
            
        private bool _disposed;
 	    // ──────────────────────────────
        #region Constructor

        public StrategyDebouncer(int delayMs, Func<Account, Instrument, long, double, double, Task> onFired,
            Func<Func<Task>, DispatcherPriority, Task> runOnUiThreadAsync, IStrategyLogger logger)
        {
            if (delayMs < 0) throw new ArgumentOutOfRangeException("delayMs");
            if (onFired == null) throw new ArgumentNullException("onFired");
 
            _delayMs = delayMs;
            _onFired = onFired;
            _runOnUiThreadAsync = runOnUiThreadAsync;
            _logger = logger ?? NullLogger.Instance;
        }
 
        #endregion
 	    // ──────────────────────────────
        #region Public API

        public void Schedule(Account account, Instrument instrument, long strategyId, double pointValue, double tickSize)
        {
            if (_disposed) return;
 
            CancellationTokenSource cts;
            lock (_lock)
            {
                if (_disposed) return;
 
                CancellationTokenSource existing;
                if (_pending.TryGetValue(strategyId, out existing))
                {
                    try { existing.Cancel();  } catch { /* ignore */ }
                    try { existing.Dispose(); } catch { /* ignore */ }
                    _pending.Remove(strategyId);
                }
 
                cts = new CancellationTokenSource();
                _pending[strategyId] = cts;
            }
 
            _ = RunAsync(account, instrument, strategyId, pointValue, tickSize, cts);
        }

        public void CancelAll()
        {
            lock (_lock)
            {
                foreach (var cts in _pending.Values)
                {
                    try { cts.Cancel();  } catch { /* ignore */ }
                    try { cts.Dispose(); } catch { /* ignore */ }
                }
                _pending.Clear();
            }
        }
 
        #endregion
 	    // ──────────────────────────────
        #region Core
 
        private async Task RunAsync(Account account,Instrument instrument,
			long strategyId, double pointValue,double tickSize, CancellationTokenSource cts)
        {
            try
            {
                await Task.Delay(_delayMs, cts.Token).ConfigureAwait(false);
                if (cts.Token.IsCancellationRequested) return;
 
                if (_runOnUiThreadAsync != null)
                    await _runOnUiThreadAsync(
                        () => _onFired(account, instrument, strategyId, pointValue, tickSize),
                        DispatcherPriority.Normal).ConfigureAwait(false);
                else
                    await _onFired(account, instrument, strategyId, pointValue, tickSize)
                        .ConfigureAwait(false);
            }
            catch (TaskCanceledException) { /* expected on cancel */ }
            catch (Exception ex) { _logger.Error($"StrategyDebouncer.RunAsync error for strategyId={strategyId}: {ex.Message}"); }
            finally
            {
                lock (_lock)
                {
                    CancellationTokenSource current;
                    if (_pending.TryGetValue(strategyId, out current) && ReferenceEquals(current, cts))                       
                    {
                        _pending.Remove(strategyId);
                        try { cts.Dispose(); } catch { /* ignore */ }
                    }
                }
            }
        }
 
        #endregion
 	    // ──────────────────────────────
        #region Dispose
 
        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                foreach (var cts in _pending.Values)
                {
                    try { cts.Cancel();  } catch { /* ignore */ }
                    try { cts.Dispose(); } catch { /* ignore */ }
                }
                _pending.Clear();
            }
        }
 
        #endregion
	    // ──────────────────────────────		
    }	
	
	#endregion		
	// ==============================	
    #region ChangeDetectingCache

    public sealed class ChangeDetectingCache<TKey, TValue>
    {
        private readonly Dictionary<TKey, TValue> _values;
        private readonly Dictionary<TKey, long> _hashes;
        private readonly object _lock = new object();

        public ChangeDetectingCache(IEnumerable<TKey> keys, TValue initialValue)
        {
            _values = keys.ToDictionary(k => k, k => initialValue);
            _hashes = keys.ToDictionary(k => k, k => 0L);
        }

        public TValue Get(TKey key)
        {
            lock (_lock) return _values.TryGetValue(key, out var v) ? v : default;
        }

        public void Set(TKey key, TValue value)
        {
            lock (_lock) _values[key] = value;
        }

        public long GetHash(TKey key)
        {
            lock (_lock) return _hashes.TryGetValue(key, out var h) ? h : 0L;
        }

        public void SetHash(TKey key, long hash)
        {
            lock (_lock) _hashes[key] = hash;
        }

        public void ResetHashes()
        {
            lock (_lock)
            {
                var keys = _hashes.Keys.ToList();
                foreach (var k in keys) _hashes[k] = 0L;
            }
        }
    }

    #endregion
	// ==============================
    #region BoundedFifoCache

    public sealed class BoundedFifoCache<TKey, TValue>
    {
        private readonly int _maxCount;
        private readonly Dictionary<TKey, TValue> _map = new Dictionary<TKey, TValue>();
        private readonly Dictionary<TKey, LinkedListNode<TKey>> _nodes = new Dictionary<TKey, LinkedListNode<TKey>>();
        private readonly LinkedList<TKey> _order = new LinkedList<TKey>();
        private readonly object _lock = new object();

        public BoundedFifoCache(int maxCount)
        {
            _maxCount = maxCount;
        }

        public int Count { get { lock (_lock) return _map.Count; } }

        public bool TryGet(TKey key, out TValue value)
        {
            lock (_lock) return _map.TryGetValue(key, out value);
        }

        public void Set(TKey key, TValue value)
        {
            lock (_lock)
            {
                if (_nodes.TryGetValue(key, out var existingNode))
                    _order.Remove(existingNode);

                _nodes[key] = _order.AddLast(key);
                _map[key] = value;

                while (_map.Count > _maxCount && _order.Count > 0)
                {
                    var oldest = _order.First.Value;
                    _order.RemoveFirst();
                    _nodes.Remove(oldest);
                    _map.Remove(oldest);
                }
            }
        }

        public void Remove(TKey key)
        {
            lock (_lock)
            {
                if (_nodes.TryGetValue(key, out var node))
                {
                    _order.Remove(node);
                    _nodes.Remove(key);
                }
                _map.Remove(key);
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _map.Clear();
                _nodes.Clear();
                _order.Clear();
            }
        }

        public List<KeyValuePair<TKey, TValue>> GetAll()
        {
            lock (_lock) return _map.ToList();
        }
    }

    #endregion	
	// ==============================
    #endregion
	// ==========================================================
}
