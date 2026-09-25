using System.Collections.Generic;

using UnityEngine;
using UnityEngine.UIElements;

namespace MaxQ.Game.Map.Overlay;

/// <summary>Map overlay: a clock with warp ticks, and labels pinned to points in the scene. Display only.</summary>
public sealed class Hud {

    public readonly struct Marker {

        public readonly Vector3 Position;
        public readonly string Text;
        public readonly string Detail;
        public readonly string Kind;

        public Marker(Vector3 position, string text, string kind, string detail = null) {

            Position = position;
            Text = text;
            Detail = detail;
            Kind = kind;

        }

    }

    // Clearance kept around each placed label.
    private const float DeclutterMargin = 4.0f;

    private readonly VisualElement _root;
    private readonly VisualElement _markerLayer;
    private readonly List<VisualElement> _markers = new List<VisualElement>();
    private readonly List<Rect> _placed = new List<Rect>();

    private readonly Label _day;
    private readonly Label _time;
    private readonly VisualElement[] _ticks;
    private readonly Label _warp;

    public Hud(UIDocument document, StyleSheet style, int warpLevels) {

        _root = document.rootVisualElement;
        _root.styleSheets.Add(style);
        _root.AddToClassList("hud");
        _root.pickingMode = PickingMode.Ignore;

        _markerLayer = Add(_root, new VisualElement(), "markers");

        VisualElement clock = Add(_root, new VisualElement(), "clock");
        _day = Add(clock, new Label(), "day");
        _time = Add(clock, new Label(), "time");

        VisualElement warp = Add(clock, new VisualElement(), "warp");
        _ticks = new VisualElement[warpLevels];

        for (int i = 0; i < warpLevels; i++) {

            _ticks[i] = Add(warp, new VisualElement(), "tick");

        }

        _warp = Add(warp, new Label(), "rate");

    }

    public void SetClock(string day, string time, string warp, int warpIndex) {

        _day.text = day;
        _time.text = time;

        _warp.text = warp;
        _warp.EnableInClassList("active", warpIndex > 0);

        for (int i = 0; i < _ticks.Length; i++) {

            _ticks[i].EnableInClassList("on", i <= warpIndex);

        }

    }

    /// <summary>Markers are placed in order; later ones yield to earlier ones when they overlap.</summary>
    public void SetMarkers(IReadOnlyList<Marker> markers, Camera camera) {

        while (_markers.Count < markers.Count) {

            VisualElement marker = Add(_markerLayer, new VisualElement(), "marker");
            Add(marker, new VisualElement(), "glyph");
            VisualElement text = Add(marker, new VisualElement(), "text");
            Add(text, new Label(), "title");
            Add(text, new Label(), "detail");
            _markers.Add(marker);

        }

        _placed.Clear();

        for (int i = 0; i < _markers.Count; i++) {

            VisualElement marker = _markers[i];

            if (i >= markers.Count || camera.WorldToViewportPoint(markers[i].Position).z <= 0.0f) {

                marker.style.display = DisplayStyle.None;

                continue;

            }

            Label detail = marker.Q<Label>(className: "detail");
            marker.Q<Label>(className: "title").text = markers[i].Text;
            detail.text = markers[i].Detail;
            detail.style.display = string.IsNullOrEmpty(markers[i].Detail) ? DisplayStyle.None : DisplayStyle.Flex;

            marker.ClearClassList();
            marker.AddToClassList("marker");
            marker.AddToClassList(markers[i].Kind);

            Vector2 panel = RuntimePanelUtils.CameraTransformWorldToPanel(_root.panel, markers[i].Position, camera);
            Rect bounds = Bounds(marker, panel);
            bool crowded = Crowded(bounds);

            // Hidden, not removed: a crowded label keeps its layout, so its box stays measurable.
            marker.style.display = DisplayStyle.Flex;
            marker.style.visibility = crowded ? Visibility.Hidden : Visibility.Visible;
            marker.style.left = panel.x;
            marker.style.top = panel.y;

            if (!crowded) {

                _placed.Add(bounds);

            }

        }

    }

    // The label box from last frame's layout, moved to this frame's anchor; the glyph is always included.
    private static Rect Bounds(VisualElement marker, Vector2 anchor) {

        Rect glyph = new Rect(anchor.x - 8.0f, anchor.y - 8.0f, 16.0f, 16.0f);
        Rect text = marker[1].layout;

        if (float.IsNaN(text.width) || text.width <= 0.0f) {

            return glyph;

        }

        Rect label = new Rect(anchor + text.position, text.size);

        return Rect.MinMaxRect(Mathf.Min(glyph.xMin, label.xMin), Mathf.Min(glyph.yMin, label.yMin), Mathf.Max(glyph.xMax, label.xMax), Mathf.Max(glyph.yMax, label.yMax));

    }

    private bool Crowded(Rect bounds) {

        Rect padded = new Rect(bounds.x - DeclutterMargin, bounds.y - DeclutterMargin, bounds.width + 2.0f * DeclutterMargin, bounds.height + 2.0f * DeclutterMargin);

        foreach (Rect other in _placed) {

            if (other.Overlaps(padded)) {

                return true;

            }

        }

        return false;

    }

    private static T Add<T>(VisualElement parent, T child, params string[] classes) where T : VisualElement {

        foreach (string name in classes) {

            child.AddToClassList(name);

        }

        child.pickingMode = PickingMode.Ignore;
        parent.Add(child);

        return child;

    }

}
