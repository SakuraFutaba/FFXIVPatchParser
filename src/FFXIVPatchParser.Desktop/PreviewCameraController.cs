using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace FFXIVPatchParser.Desktop;

internal sealed class PreviewCameraController
{
    internal readonly record struct ViewState(Point3D Position, double Yaw, double Pitch, Point3D? Pivot = null);

    private readonly FrameworkElement surface;
    private readonly Viewport3D viewport;
    private readonly PerspectiveCamera camera;
    private readonly double initialYaw, initialPitch;
    private readonly HashSet<Key> navigationKeys = [];
    private Rect3D sceneBounds = Rect3D.Empty;
    private Rect3D frameBounds = Rect3D.Empty;
    private Point3D cameraPosition;
    private Point3D orbitCenter;
    private Point? dragOrigin;
    private double yaw, pitch, navigationScale;
    private double? targetDistance;
    private double zoomDistance;
    private bool leftHeld, rightHeld, middleHeld, cameraMoved;
    private bool renderingInput;
    private TimeSpan? lastInputFrame;

    private Vector3D Facing => new(-Math.Sin(yaw) * Math.Cos(pitch), -Math.Sin(pitch), -Math.Cos(yaw) * Math.Cos(pitch));
    private Vector3D CameraRight => new(Math.Cos(yaw), 0, -Math.Sin(yaw));
    private Vector3D CameraUp => Vector3D.CrossProduct(CameraRight, Facing);

    public PreviewCameraController(FrameworkElement surface, Viewport3D viewport, PerspectiveCamera camera,
        double initialYaw, double initialPitch)
    {
        this.surface = surface;
        this.viewport = viewport;
        this.camera = camera;
        this.initialYaw = yaw = initialYaw;
        this.initialPitch = pitch = initialPitch;
        InputMethod.SetIsInputMethodEnabled(surface, false);
        surface.MouseDown += StartDrag;
        surface.MouseUp += EndDrag;
        surface.MouseMove += Drag;
        surface.LostMouseCapture += CancelDrag;
        surface.MouseWheel += Zoom;
        surface.PreviewKeyDown += PressNavigationKey;
        surface.PreviewKeyUp += ReleaseNavigationKey;
        surface.LostKeyboardFocus += LoseNavigationFocus;
        surface.IsVisibleChanged += (_, _) => { if (!surface.IsVisible) StopInput(); };
        viewport.SizeChanged += ResizeScene;
    }

    public void SetBounds(Rect3D bounds, Rect3D preferredBounds)
    {
        var first = sceneBounds.IsEmpty;
        sceneBounds = bounds;
        frameBounds = preferredBounds.IsEmpty ? bounds : preferredBounds;
        if (bounds.IsEmpty) StopInput();
        else if (!cameraMoved || first) FrameScene(first);
        else UpdateCamera();
    }

    public void Reset()
    {
        StopInput();
        FrameScene(resetAngle: true);
    }

    public void ResetAtDistance(double distance)
    {
        if (sceneBounds.IsEmpty) return;
        StopInput();
        cameraPosition = orbitCenter - Facing * distance;
        cameraMoved = true;
        UpdateCamera();
    }

    public Point3D Position => cameraPosition;

    public ViewState CaptureView() => new(cameraPosition, yaw, pitch, orbitCenter);

    public void RestoreView(ViewState view)
    {
        StopInput();
        cameraPosition = view.Position;
        yaw = view.Yaw;
        pitch = view.Pitch;
        orbitCenter = view.Pivot ?? cameraPosition + Facing * 20;
        cameraMoved = true;
        UpdateCamera();
    }

    public void GoTo(Point3D position)
    {
        if (sceneBounds.IsEmpty) return;
        RestoreView(new(position, initialYaw, 0.08));
    }

    private void FrameScene(bool resetAngle)
    {
        if (sceneBounds.IsEmpty) return;
        if (resetAngle)
        {
            yaw = initialYaw;
            pitch = initialPitch;
            cameraMoved = false;
        }
        var bounds = frameBounds;
        var size = new Vector3D(bounds.SizeX, bounds.SizeY, bounds.SizeZ);
        orbitCenter = bounds.Location + size / 2;
        var aspect = viewport.ActualWidth > 0 && viewport.ActualHeight > 0 ? viewport.ActualWidth / viewport.ActualHeight : 1;
        var horizontal = Math.Tan(camera.FieldOfView * Math.PI / 360);
        var distance = Math.Max(Project(size, CameraRight), Project(size, CameraUp) * aspect) / (2 * horizontal)
            + Project(size, Facing) * 0.36;
        cameraPosition = orbitCenter - Facing * Math.Max(2, distance);
        navigationScale = Math.Max(0.1, size.Length);
        UpdateCamera();
    }

    private void ResizeScene(object sender, SizeChangedEventArgs e)
    {
        if (!cameraMoved) FrameScene(resetAngle: false);
        else UpdateCamera();
    }

    private void UpdateCamera()
    {
        if (sceneBounds.IsEmpty) return;
        var facing = Facing;
        var size = new Vector3D(sceneBounds.SizeX, sceneBounds.SizeY, sceneBounds.SizeZ);
        var centerDepth = Vector3D.DotProduct(sceneBounds.Location + size / 2 - cameraPosition, facing);
        var padding = Math.Clamp(navigationScale * 0.015, 0.025, 1);
        var radius = Project(size, facing) / 2 + padding;
        camera.Position = cameraPosition;
        camera.LookDirection = facing;
        camera.NearPlaneDistance = Math.Max(padding * 0.1, centerDepth - radius);
        camera.FarPlaneDistance = Math.Max(camera.NearPlaneDistance + padding, centerDepth + radius);
    }

    private static double Project(Vector3D size, Vector3D axis)
        => size.X * Math.Abs(axis.X) + size.Y * Math.Abs(axis.Y) + size.Z * Math.Abs(axis.Z);

    private void StartDrag(object sender, MouseButtonEventArgs e)
    {
        if (sceneBounds.IsEmpty || e.ChangedButton is not (MouseButton.Left or MouseButton.Right or MouseButton.Middle)) return;
        surface.Focus();
        if (!surface.IsMouseCaptured && !surface.CaptureMouse()) return;
        dragOrigin = e.GetPosition(surface);
        UpdateMouseButtons(e);
        e.Handled = true;
    }

    private void Drag(object sender, MouseEventArgs e)
    {
        if (dragOrigin is not { } previous) return;
        var current = e.GetPosition(surface);
        var delta = current - previous;
        dragOrigin = current;
        if (delta.LengthSquared <= 0) return;
        if (middleHeld)
        {
            var scale = DetailSpeed() / Math.Max(1, viewport.ActualWidth) * 3;
            Translate((-delta.X * CameraRight + delta.Y * CameraUp) * scale);
        }
        else if (leftHeld || rightHeld)
        {
            Rotate(-delta.X * 0.005, delta.Y * 0.005, leftHeld && !rightHeld);
        }
        cameraMoved = true;
        UpdateCamera();
    }

    private void Rotate(double yawDelta, double pitchDelta, bool orbit)
    {
        var previousPitch = pitch;
        yaw += yawDelta;
        pitch = Math.Clamp(pitch + pitchDelta, -1.55, 1.55);
        if (!orbit) return;

        var rotation = Matrix3D.Identity;
        rotation.Rotate(new Quaternion(new Vector3D(0, 1, 0), yawDelta * 180 / Math.PI));
        rotation.Rotate(new Quaternion(CameraRight, (previousPitch - pitch) * 180 / Math.PI));
        cameraPosition = orbitCenter + rotation.Transform(cameraPosition - orbitCenter);
    }

    private void Translate(Vector3D offset)
    {
        cameraPosition += offset;
        orbitCenter += offset;
    }

    private void EndDrag(object sender, MouseButtonEventArgs e)
    {
        if (!surface.IsMouseCaptured) return;
        UpdateMouseButtons(e);
        if (!leftHeld && !rightHeld && !middleHeld) surface.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void UpdateMouseButtons(MouseEventArgs e)
    {
        leftHeld = e.LeftButton == MouseButtonState.Pressed;
        rightHeld = e.RightButton == MouseButtonState.Pressed;
        middleHeld = e.MiddleButton == MouseButtonState.Pressed;
        UpdateInputSubscription();
    }

    private void CancelDrag(object sender, MouseEventArgs e)
    {
        dragOrigin = null;
        leftHeld = rightHeld = middleHeld = false;
        UpdateInputSubscription();
    }

    private void StopInput()
    {
        navigationKeys.Clear();
        targetDistance = null;
        dragOrigin = null;
        leftHeld = rightHeld = middleHeld = false;
        if (surface.IsMouseCaptured) surface.ReleaseMouseCapture();
        UpdateInputSubscription();
    }

    private void LoseNavigationFocus(object sender, KeyboardFocusChangedEventArgs e) => StopInput();

    private void Zoom(object sender, MouseWheelEventArgs e)
    {
        if (sceneBounds.IsEmpty || e.Delta == 0) return;
        NudgeZoom(e.Delta / 120.0);
        e.Handled = true;
    }

    private void NudgeZoom(double notches)
    {
        if (!targetDistance.HasValue) zoomDistance = (cameraPosition - orbitCenter).Length;
        var distance = targetDistance ?? zoomDistance;
        var minimum = Math.Clamp(navigationScale * 0.0002, 0.005, 0.25);
        var maximum = Math.Max(distance, Math.Max(100, navigationScale * 12));
        targetDistance = Math.Clamp(distance * Math.Exp(-0.12 * notches * SpeedMultiplier()), minimum, maximum);
        cameraMoved = true;
        UpdateInputSubscription();
    }

    private bool StepZoom(double deltaTime)
    {
        if (targetDistance is not { } target) return false;
        var distance = zoomDistance;
        var next = target + (distance - target) * Math.Exp(-12 * deltaTime);
        if (Math.Abs(next - target) < Math.Max(0.00005, target * 0.0005))
        {
            next = target;
            targetDistance = null;
            UpdateInputSubscription();
        }
        // 原地转向后旋转中心可以偏离视轴；滚轮沿当前朝向推进，保留独立的环绕中心。
        cameraPosition += Facing * (distance - next);
        zoomDistance = next;
        return true;
    }

    private void PressNavigationKey(object sender, KeyEventArgs e)
    {
        var key = NavigationKey(e);
        if (sceneBounds.IsEmpty || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) ||
            !IsMovementKey(key) && !IsSpeedKey(key)) return;
        navigationKeys.Add(key);
        UpdateInputSubscription();
        e.Handled = true;
    }

    private void ReleaseNavigationKey(object sender, KeyEventArgs e)
    {
        if (!navigationKeys.Remove(NavigationKey(e))) return;
        UpdateInputSubscription();
        e.Handled = true;
    }

    private void UpdateInputSubscription()
    {
        var needsInput = targetDistance.HasValue || (leftHeld && rightHeld) || navigationKeys.Any(IsMovementKey);
        if (renderingInput == needsInput) return;
        renderingInput = needsInput;
        lastInputFrame = null;
        if (needsInput) CompositionTarget.Rendering += AdvanceCamera;
        else CompositionTarget.Rendering -= AdvanceCamera;
    }

    private static Key NavigationKey(KeyEventArgs e) => e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;

    private static bool IsMovementKey(Key key) => key is Key.W or Key.A or Key.S or Key.D or Key.Space or Key.Q
        or Key.Left or Key.Right or Key.Up or Key.Down;

    private static bool IsSpeedKey(Key key) => key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl;

    private void AdvanceCamera(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs frame) return;
        var deltaTime = lastInputFrame is { } previous
            ? Math.Clamp((frame.RenderingTime - previous).TotalSeconds, 0, 0.05) : 0;
        lastInputFrame = frame.RenderingTime;
        if (deltaTime <= 0) return;
        StepCamera(deltaTime);
        if (StepZoom(deltaTime)) UpdateCamera();
    }

    private void StepCamera(double deltaTime)
    {
        int Axis(Key plus, Key minus) => (navigationKeys.Contains(plus) ? 1 : 0) - (navigationKeys.Contains(minus) ? 1 : 0);
        var horizontal = Axis(Key.D, Key.A);
        var strafe = leftHeld || rightHeld;
        var turn = Axis(Key.Right, Key.Left) + (strafe ? 0 : horizontal);
        var tilt = Axis(Key.Down, Key.Up);
        Rotate(-turn * 1.65 * deltaTime, tilt * 1.45 * deltaTime, orbit: false);
        var advancing = navigationKeys.Contains(Key.W) || (leftHeld && rightHeld);
        var forward = (advancing ? 1 : 0) - (navigationKeys.Contains(Key.S) ? 1 : 0);
        var velocity = Facing * forward + CameraRight * (strafe ? horizontal : 0) + new Vector3D(0, Axis(Key.Space, Key.Q), 0);
        if (velocity.LengthSquared > 1) velocity.Normalize();
        if (velocity.LengthSquared == 0 && turn == 0 && tilt == 0) return;
        Translate(velocity * (DetailSpeed() * SpeedMultiplier() * deltaTime));
        cameraMoved = true;
        UpdateCamera();
    }

    private double SpeedMultiplier()
    {
        var modifiers = Keyboard.Modifiers;
        var shift = navigationKeys.Contains(Key.LeftShift) || navigationKeys.Contains(Key.RightShift) || modifiers.HasFlag(ModifierKeys.Shift);
        var ctrl = navigationKeys.Contains(Key.LeftCtrl) || navigationKeys.Contains(Key.RightCtrl) || modifiers.HasFlag(ModifierKeys.Control);
        return shift == ctrl ? 1 : shift ? 10.0 : 0.1;
    }

    private double DetailSpeed()
    {
        var orbit = frameBounds;
        var nearest = new Point3D(Math.Clamp(cameraPosition.X, orbit.X, orbit.X + orbit.SizeX),
            Math.Clamp(cameraPosition.Y, orbit.Y, orbit.Y + orbit.SizeY),
            Math.Clamp(cameraPosition.Z, orbit.Z, orbit.Z + orbit.SizeZ));
        var minimum = Math.Clamp(navigationScale * 0.04, 0.2, 7);
        return Math.Clamp(minimum + (cameraPosition - nearest).Length * 0.22, minimum, Math.Max(minimum * 2, navigationScale * 0.42));
    }
}
