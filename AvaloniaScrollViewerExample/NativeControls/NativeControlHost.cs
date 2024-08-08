using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AvaloniaScrollViewerExample.NativeControls;

public class NativeControlHost : Control
{
    // Magic number, might need to be adjusted
    private const double SHOW_ATTACHMENT_IN_BOUNDS_CHUNK_SIZE = 0.05;

    // Magic number, might need to be adjusted
    private const int SHOW_ATTACHMENT_IN_BOUNDS_MAX_DISTANCE = 10;

    // normal
    // send x
    // input
    //
    private readonly DispatcherPriority _priority = DispatcherPriority.Normal;//.FromValue(3);

    // Keep track of last bounds to avoid unnecessary updates
    private double _lastBoundsY = 0;

    private TopLevel? _currentRoot;
    private INativeControlHostImpl? _currentHost;
    private INativeControlHostControlTopLevelAttachment? _attachment;
    private IPlatformHandle? _nativeControlHandle;
    private bool _queuedForDestruction;
    private bool _queuedForMoveResize;
    private readonly List<Visual> _propertyChangedSubscriptions = new();

    static NativeControlHost()
    {
        FlowDirectionProperty.Changed.AddClassHandler<NativeControlHost>(OnFlowDirectionChanged);
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _currentRoot = e.Root as TopLevel;
        var visual = (Visual)this;
        while (visual != null)
        {
            visual.PropertyChanged += PropertyChangedHandler;
            _propertyChangedSubscriptions.Add(visual);

            visual = visual.GetVisualParent();
        }

        // Reset the last bounds
        _lastBoundsY = 0;

        UpdateHost();
    }

    private static void OnFlowDirectionChanged(NativeControlHost nativeControlHost,
        AvaloniaPropertyChangedEventArgs propertyChangedEventArgs)
    {
        nativeControlHost.TryUpdateNativeControlPosition();
    }

    private void PropertyChangedHandler(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if ((e.Property == BoundsProperty || e.Property == IsVisibleProperty))
            EnqueueForMoveResize();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _currentRoot = null;
        foreach (var v in _propertyChangedSubscriptions)
            v.PropertyChanged -= PropertyChangedHandler;
        _propertyChangedSubscriptions.Clear();
        UpdateHost();
    }


    private void UpdateHost()
    {
        _queuedForMoveResize = false;
        _currentHost = _currentRoot?.PlatformImpl?.TryGetFeature<INativeControlHostImpl>();

        if (_currentHost != null)
        {
            // If there is an existing attachment, ensure that we are attached to the proper host or destroy the attachment
            if (_attachment != null && _attachment.AttachedTo != _currentHost)
            {
                if (_attachment != null)
                {
                    if (_attachment.IsCompatibleWith(_currentHost))
                    {
                        _attachment.AttachedTo = _currentHost;
                    }
                    else
                    {
                        _attachment.Dispose();
                        _attachment = null;
                    }
                }
            }

            // If there is no attachment, but the control exists,
            // attempt to attach to to the current toplevel or destroy the control if it's incompatible
            if (_attachment == null && _nativeControlHandle != null)
            {
                if (_currentHost.IsCompatibleWith(_nativeControlHandle))
                    _attachment = _currentHost.CreateNewAttachment(_nativeControlHandle);
                else
                    DestroyNativeControl();
            }

            // There is no control handle an no attachment, create both
            if (_nativeControlHandle == null)
            {
                _attachment = _currentHost.CreateNewAttachment(parent =>
                    _nativeControlHandle = CreateNativeControlCore(parent));
            }
        }
        else
        {
            // Immediately detach the control from the current toplevel if there is an existing attachment
            if (_attachment != null)
                _attachment.AttachedTo = null;

            // Don't destroy the control immediately, it might be just being reparented to another TopLevel
            if (_nativeControlHandle != null && !_queuedForDestruction)
            {
                _queuedForDestruction = true;
                Dispatcher.UIThread.Post(CheckDestruction, _priority);
            }
        }

        if (_attachment?.AttachedTo != _currentHost)
            return;

        TryUpdateNativeControlPosition();
    }


    private Rect? GetAbsoluteBounds()
    {
        Debug.Assert(_currentRoot is not null);

        var bounds = Bounds;
        // Native window is not rendered by Avalonia
        var transformToVisual = this.TransformToVisual(_currentRoot);
        if (transformToVisual == null)
            return null;
        var position = new Rect(default, bounds.Size).TransformToAABB(transformToVisual.Value).Position;
        return new Rect(position, bounds.Size);

        // Debug.Assert(_currentRoot is not null);
        //
        // var bounds = Bounds;
        // var position = this.TranslatePoint(default, _currentRoot);
        // if (position == null)
        //     return null;
        // return new Rect(position.Value, bounds.Size);
    }

    private void EnqueueForMoveResize()
    {
        if (_queuedForMoveResize)
            return;
        _queuedForMoveResize = true;
        Dispatcher.UIThread.Post(UpdateHost, _priority);
    }

    public bool TryUpdateNativeControlPosition()
    {
        if (_currentHost == null)
            return false;

        var bounds = GetAbsoluteBounds();

        if (IsEffectivelyVisible && bounds.HasValue)
        {
            if (bounds.Value.Width == 0 && bounds.Value.Height == 0)
                return false;

            var deltaY = bounds.Value.Y - _lastBoundsY;
            var absDeltaY = Math.Abs(deltaY);

            // If the control is not moving vertically or the movement is too big, show the control immediately
            if (_lastBoundsY is 0 || absDeltaY is 0 || absDeltaY > SHOW_ATTACHMENT_IN_BOUNDS_MAX_DISTANCE)
            {
                _attachment?.ShowInBounds(bounds.Value);
            }
            else
            {
                _ = ShowAttachmentInBoundsInChunksAsync(absDeltaY, deltaY, bounds.Value, _lastBoundsY);
            }

            _lastBoundsY = bounds.Value.Y;
        }
        else
        {
            _attachment?.HideWithSize(Bounds.Size);
        }

        return true;
    }

    private async Task ShowAttachmentInBoundsInChunksAsync(
        double absDeltaY,
        double deltaY,
        Rect currentBounds,
        double lastBoundsY)
    {
        var localAttachment = _attachment;

        if (localAttachment == null)
            return;

        //await Task.Delay(_showAttachmentInBoundsInChunksDelay);

        if (!AttachmentIsValidToShowInBounds())
            return;

        var chunks = absDeltaY / SHOW_ATTACHMENT_IN_BOUNDS_CHUNK_SIZE;
        var remainder = absDeltaY % SHOW_ATTACHMENT_IN_BOUNDS_CHUNK_SIZE;
        var sign = Math.Sign(deltaY);

        for (var index = 0; index < chunks; index++)
        {
            var chunk = SHOW_ATTACHMENT_IN_BOUNDS_CHUNK_SIZE * (index + 1);
            ShowInBounds(chunk);
        }

        if (remainder > 0)
        {
            ShowInBounds(remainder);
        }

        return;

        void ShowInBounds(double chunk)
        {
            var newBounds = currentBounds.WithY(lastBoundsY + (chunk * sign));

            Dispatcher.UIThread.Post(
                () =>
                {
                    if (!AttachmentIsValidToShowInBounds())
                        return;

                    localAttachment.ShowInBounds(newBounds);
                },
                _priority);
        }

        bool AttachmentIsValidToShowInBounds()
        {
            // It's valid if the attachment is still the same, and it's attached to a host
            return localAttachment == _attachment && localAttachment.AttachedTo != null;
        }
    }

    private void CheckDestruction()
    {
        _queuedForDestruction = false;
        if (_currentRoot == null)
            DestroyNativeControl();
    }

    protected virtual IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (_currentHost == null)
            throw new InvalidOperationException();
        return _currentHost.CreateDefaultChild(parent);
    }

    private void DestroyNativeControl()
    {
        if (_nativeControlHandle != null)
        {
            _attachment?.Dispose();
            _attachment = null;

            DestroyNativeControlCore(_nativeControlHandle);
            _nativeControlHandle = null;
        }
    }

    protected virtual void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (control is INativeControlHostDestroyableControlHandle nativeControlHostDestroyableControlHandle)
        {
            nativeControlHostDestroyableControlHandle.Destroy();
        }
    }
}