namespace AgentSpeed.Presentation.Layout;

public readonly record struct PointD(double X, double Y);

public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public PointD Center => new(X + Width / 2, Y + Height / 2);

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(PointD point) => point.X >= X && point.X < Right && point.Y >= Y && point.Y < Bottom;

    public double DistanceSquaredTo(PointD point)
    {
        var dx = Math.Max(Math.Max(X - point.X, 0), point.X - Right);
        var dy = Math.Max(Math.Max(Y - point.Y, 0), point.Y - Bottom);
        return dx * dx + dy * dy;
    }

    public static RectD FromEdges(double left, double top, double right, double bottom) =>
        new(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
}

public enum TaskbarEdge
{
    Left,
    Top,
    Right,
    Bottom,
}
