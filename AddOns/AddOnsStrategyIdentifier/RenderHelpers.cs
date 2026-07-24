#region Using declarations
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Input;
using NinjaTrader.Cbi;
using NinjaTrader.Gui.Chart;
#endregion

using NinjaTrader.NinjaScript.AddOns.AddOnsStrategyIdentifier.StrategySnapshotManager;

//This namespace holds Add ons in this folder and is required. Do not change it. 
namespace NinjaTrader.NinjaScript.AddOns.AddOnsStrategyIdentifier.RenderHelpers
{
    // ==========================================================
	#region LevelMarkerType
		
	internal enum LevelMarkerType { Entry, Target, Stop }
	
	#endregion	
    // ==========================================================
	#region NearestLevelResolver
	
    internal class LevelMarkerRender
    {
        // Initial data
        public StrategyModel Strategy;
        public StrategyPriceLevel Level;
        public LevelMarkerType MarkerType;

        // Prepared segments (Pass 1)
        public SegmentMarkerRender.MarkerStrategySegment[] Segments;
        public float TotalWidth;
        public SharpDX.Direct2D1.Brush LineBrush;
        public SharpDX.Direct2D1.Brush BackgroundBrush;

        // Assigned after grouping (Pass 2)
        public float X;
        public float Y;
        public long PriceTickKey;
    }
	
	#endregion		
    // ==========================================================
	#region AverageMarkerRender
		
	internal class AverageMarkerRender
	{
	    public StrategyModel Strategy;
	    public PositionSnapshot Position;
	    public double AveragePrice;
	    
	    public SegmentMarkerRender.MarkerStrategySegment[] Segments;
	    public float TotalWidth;
	    public SharpDX.Direct2D1.Brush MarkerBgBrush;
	    public SharpDX.Direct2D1.Brush LineBrush;
	    
	    public float X;
	    public float Y;
	    public long PriceTickKey;
		public bool IsAggregated;
	}
	
	#endregion		
    // ==========================================================
	#region SegmentMarkerRender
			
	internal class SegmentMarkerRender
	{
	    protected readonly SharpDX.Direct2D1.RenderTarget _renderTarget;
	    protected readonly TextLayoutDXManager _textLayoutManager;
	
	    protected SharpDX.Vector2 _v1;
	    private SharpDX.Direct2D1.RoundedRectangle _reusableRoundedRect;
	
	    public SegmentMarkerRender(SharpDX.Direct2D1.RenderTarget renderTarget, TextLayoutDXManager textLayoutManager)
	    {
	        _renderTarget = renderTarget ?? throw new ArgumentNullException(nameof(renderTarget));
	        _textLayoutManager = textLayoutManager ?? throw new ArgumentNullException(nameof(textLayoutManager));
	        
	        _v1 = new SharpDX.Vector2();
	        _reusableRoundedRect = new SharpDX.Direct2D1.RoundedRectangle();
	    }
	
	    public struct MarkerStrategySegment
	    {
	        public SharpDX.DirectWrite.TextLayout Layout;
	        public float TextWidth;
	        public float Width;
	        public float Padding;
	
	        public SharpDX.Direct2D1.Brush Background;
	        public SharpDX.Direct2D1.Brush TextBrush;
			public float CornerRadius;
			
		    public SharpDX.Direct2D1.Brush BorderBrush;
		    public float BorderWidth;
		    public bool HasBorder;			
	    }
	
	    public MarkerStrategySegment BuildStrategySegment(
			string stratText, 
			float padding, 
			float height, 
			bool isBold, 
			SharpDX.Direct2D1.Brush bgBrush, 
			SharpDX.Direct2D1.Brush textBrush, 
			float radius,
		    SharpDX.Direct2D1.Brush brdBrush,
		    float brdWidth)
	    {
	        var layout = _textLayoutManager.GetLayout(stratText, height, isBold, out float textWidth);
	
	        return new MarkerStrategySegment
	        {
	            Layout = layout,
	            TextWidth = textWidth,
	            Padding = padding,
	            Width = textWidth + padding,
	            Background = bgBrush,
	            TextBrush = textBrush,
	            CornerRadius = radius,
		        BorderBrush = brdBrush,
		        BorderWidth = brdWidth,
		        HasBorder = brdBrush != null && brdWidth > 0				
	        };
	    }
	
	    public void DrawStrategySegment(float x, float yTop, float height, MarkerStrategySegment seg)
	    {
	        if (seg.Layout == null || seg.Width <= 0) return;

            float bgHeight = height;
            float bgTop = yTop + (height - bgHeight) * 0.5f;
			
			float maxRadius = height * 0.5f;
			float radius = Math.Min(seg.CornerRadius, maxRadius);

            var rect = MakeRoundedRect(x, bgTop, seg.Width, bgHeight, radius);
		
	        if (seg.Background != null)			
	            _renderTarget.FillRoundedRectangle(rect, seg.Background);
		
	        if (seg.HasBorder && seg.BorderBrush != null)
				WithAntialias(SharpDX.Direct2D1.AntialiasMode.PerPrimitive, () => _renderTarget.DrawRoundedRectangle(rect, seg.BorderBrush, seg.BorderWidth));			

	        float textX = x + (seg.Width - seg.TextWidth) * 0.5f;
	        float textY = yTop + (height - seg.Layout.Metrics.Height) * 0.5f;
	
	        _v1.X = textX;
	        _v1.Y = textY;
	
	        _renderTarget.DrawTextLayout(_v1, seg.Layout, seg.TextBrush);
	    }
	
	    public SharpDX.Direct2D1.RoundedRectangle MakeRoundedRect(float left, float top, float width, float height, float radius)
	    {
	        _reusableRoundedRect.Rect = new SharpDX.RectangleF(left, top, width, height);
	        _reusableRoundedRect.RadiusX = radius;
	        _reusableRoundedRect.RadiusY = radius;
	        return _reusableRoundedRect;
	    }
		
		void WithAntialias(SharpDX.Direct2D1.AntialiasMode mode, Action draw)
		{
		    var old = _renderTarget.AntialiasMode;
		    try { _renderTarget.AntialiasMode = mode; draw(); }
		    finally { _renderTarget.AntialiasMode = old; } 
		}		
	}
	
	#endregion		
    // ==========================================================
	#region SharpDXBrushManager
			
    internal class SharpDXBrushManager<TKey> : IDisposable where TKey : struct, Enum
    {
        private readonly Dictionary<TKey, SharpDXMediaMap> _dxMBrushes;
        private readonly Dictionary<TKey, SharpDX.Direct2D1.Brush> _cachedDxBrushes;
        private SharpDX.Direct2D1.RenderTarget _renderTarget;
        private readonly ReaderWriterLockSlim _rwLock = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
        private bool _disposed;

        private class SharpDXMediaMap
        {
            public SharpDX.Direct2D1.Brush DxBrush;
            public System.Windows.Media.Brush MediaBrush;
            public double Opacity;
        }

        public IReadOnlyDictionary<TKey, SharpDX.Direct2D1.Brush> CachedDxBrushes => _cachedDxBrushes;

        public SharpDXBrushManager(IDictionary<TKey, (System.Windows.Media.Brush Brush, double Opacity)> brushes)
        {
            if (brushes == null) throw new ArgumentNullException(nameof(brushes));

            _dxMBrushes = new Dictionary<TKey, SharpDXMediaMap>();
            _cachedDxBrushes = new Dictionary<TKey, SharpDX.Direct2D1.Brush>();

            foreach (var kvp in brushes)
            {
                if (kvp.Value.Brush == null) continue;

                double opacity = ClampOpacity(kvp.Value.Opacity);

                _dxMBrushes[kvp.Key] = new SharpDXMediaMap
                {
                    MediaBrush = kvp.Value.Brush,
                    Opacity = opacity
                };
            }
        }

        public void SetRenderTarget(SharpDX.Direct2D1.RenderTarget rt)
        {
            _rwLock.EnterWriteLock();
            try
            {
                ThrowIfDisposed();

                _renderTarget = rt;

                foreach (var kv in _dxMBrushes)
                {
                    try { kv.Value.DxBrush?.Dispose(); } catch { /* ignore */ }
                    kv.Value.DxBrush = null;
                }

                _cachedDxBrushes.Clear();

                if (_renderTarget == null || _renderTarget.IsDisposed) return;

                foreach (var kv in _dxMBrushes)
                {
                    var map = kv.Value;
                    if (map.MediaBrush == null) continue;

                    map.DxBrush = CreateDxBrush(map.MediaBrush, map.Opacity);

                    if (map.DxBrush != null)
                        _cachedDxBrushes[kv.Key] = map.DxBrush;
                }
            }
            finally { _rwLock.ExitWriteLock(); }
        }

        public SharpDX.Direct2D1.Brush GetCachedDxBrush(TKey key)
        {
            _rwLock.EnterReadLock();
            try
            {
                ThrowIfDisposed();
                _cachedDxBrushes.TryGetValue(key, out var brush);
                return brush;
            }
            catch (ObjectDisposedException) { return null; }
            finally { _rwLock.ExitReadLock(); }
        }

        public void UpdateBrush(TKey key, System.Windows.Media.Brush mediaBrush, double opacity)
        {
            if (mediaBrush == null) throw new ArgumentNullException(nameof(mediaBrush));
            opacity = ClampOpacity(opacity);

            _rwLock.EnterWriteLock();
            try
            {
                ThrowIfDisposed();

                if (!_dxMBrushes.ContainsKey(key))
                    throw new ArgumentException($"SharpDXBrushManager.UpdateBrush: Brush '{key}' not found");

                var map = _dxMBrushes[key];

                map.MediaBrush = mediaBrush;
                map.Opacity = opacity;

                try { map.DxBrush?.Dispose(); } catch { /* ignore */ }
                map.DxBrush = null;

                _cachedDxBrushes.Remove(key);

                if (_renderTarget != null && !_renderTarget.IsDisposed)
                {
                    map.DxBrush = CreateDxBrush(mediaBrush, opacity);
                    if (map.DxBrush != null)
                        _cachedDxBrushes[key] = map.DxBrush;
                }
            }
            finally { _rwLock.ExitWriteLock(); }
        }

		private SharpDX.Direct2D1.Brush CreateDxBrush(System.Windows.Media.Brush mediaBrush, double opacity)
		{
		    var scb = mediaBrush as System.Windows.Media.SolidColorBrush;
		    if (scb == null) return null;
		
		    var c = scb.Color;

		    float colorAlpha = c.A / 255f;
		    float opacityAlpha = (float)(opacity / 100.0);
		    float alpha = colorAlpha * opacityAlpha;
		
		    return new SharpDX.Direct2D1.SolidColorBrush(
		        _renderTarget,
		        new SharpDX.Color4(
		            c.R / 255f,
		            c.G / 255f,
		            c.B / 255f,
		            alpha
		        )
		    );
		}

        private void ThrowIfDisposed() 
        {
            if (_disposed) throw new ObjectDisposedException(GetType().Name);
        }

        private static double ClampOpacity(double value)
        {
            return value < 0 ? 0 : value > 100 ? 100 : value;
        }

        public void Dispose()
        {
            if (!_rwLock.TryEnterWriteLock(TimeSpan.FromSeconds(1)))
            {
                try
                {
                    foreach (var kv in _dxMBrushes)
                        try { kv.Value.DxBrush?.Dispose(); } catch { /* ignore */ }
                }
                catch { /* ignore */ }
                return;
            }

            try
            {
                if (_disposed) return;

                foreach (var kv in _dxMBrushes)
                {
                    try { kv.Value.DxBrush?.Dispose(); } catch { /* ignore */ }
                }

                _dxMBrushes.Clear();
                _cachedDxBrushes.Clear();

                _disposed = true;
            }
            finally
            {
                _rwLock.ExitWriteLock();
                _rwLock.Dispose();
            }

            GC.SuppressFinalize(this);
        }
    }
	
	#endregion		
    // ==========================================================
	#region TextLayoutDXManager
		
	internal class TextLayoutDXManager : IDisposable
	{
	    private readonly SharpDX.DirectWrite.Factory _factory;
	    private SharpDX.DirectWrite.TextFormat _textFormat;
	
	    private readonly int _capacity;
	
	    private readonly Dictionary<LayoutKey, LinkedListNode<CacheItem>> _map;
	    private readonly LinkedList<CacheItem> _lruList;

	    private struct LayoutKey : IEquatable<LayoutKey>
	    {
	        public readonly string Text;
	        public readonly float Height;
	        public readonly bool Bold;
	
	        public LayoutKey(string text, float height, bool bold)
	        {
	            Text = text;
	            Height = (int)(height * 10);
	            Bold = bold;
	        }
	
	        public bool Equals(LayoutKey other)
	        {
	            return Text == other.Text &&
	                   Height == other.Height &&
	                   Bold == other.Bold;
	        }
	
	        public override int GetHashCode()
	        {
	            unchecked
	            {
	                int hash = 17;
	
	                if (Text != null)
	                    hash = hash * 31 + Text.GetHashCode();
	
	                hash = hash * 31 + Height.GetHashCode();
	                hash = hash * 31 + Bold.GetHashCode();
	
	                return hash;
	            }
	        }
	    }
	
	    private class CacheItem
	    {
	        public LayoutKey Key;
	        public SharpDX.DirectWrite.TextLayout Layout;
	        public float Width;
	    }
	
	    public TextLayoutDXManager(SharpDX.DirectWrite.Factory factory, int capacity = 128)
	    {
	        _factory = factory;
	        _capacity = capacity;
	
	        _map = new Dictionary<LayoutKey, LinkedListNode<CacheItem>>(capacity);
	        _lruList = new LinkedList<CacheItem>();
	    }
	
	    public void SetTextFormat(SharpDX.DirectWrite.TextFormat format)
	    {
	        if (_textFormat == format) return;
	
	        _textFormat = format;
	        Clear();
	    }
	
	    public SharpDX.DirectWrite.TextLayout GetLayout(string text, float height, bool bold, out float width)
	    {
	        width = 0;
	        
	        if (_textFormat == null || string.IsNullOrEmpty(text))
	            return null;
	
	        var key = new LayoutKey(text, height, bold);
	
	        if (_map.TryGetValue(key, out var node))
	        {
	            _lruList.Remove(node);
	            _lruList.AddFirst(node);
	
	            width = node.Value.Width;
	            return node.Value.Layout;
	        }

			float maxWidth = float.MaxValue;
            var layout = new SharpDX.DirectWrite.TextLayout(_factory, text, _textFormat, maxWidth, height);

            if (bold)
            {
                layout.SetFontWeight(
                    SharpDX.DirectWrite.FontWeight.Bold,
                    new SharpDX.DirectWrite.TextRange(0, text.Length));
            }
            
            layout.TextAlignment = SharpDX.DirectWrite.TextAlignment.Leading;

            width = layout.Metrics.WidthIncludingTrailingWhitespace;

            var item = new CacheItem
            {
                Key = key,
                Layout = layout,
                Width = width
            };

            var newNode = new LinkedListNode<CacheItem>(item);
            _lruList.AddFirst(newNode);
            _map[key] = newNode;

            if (_map.Count > _capacity)
            {
                var last = _lruList.Last;
                if (last != null)
                {
                    _lruList.RemoveLast();
                    _map.Remove(last.Value.Key);

                    try { last.Value.Layout?.Dispose(); } catch { /* ignore */ } 
                }
            }

            return layout;
	    }
	
	    public void Clear()
	    {
            foreach (var node in _lruList)
            {
               node.Layout?.Dispose();
            }

            _lruList.Clear();
            _map.Clear();
	    }
	
	    public void Dispose() => Clear(); 
	}
	
	#endregion
    // ==========================================================
}
