using Avalonia.Platform;

namespace AvaloniaScrollViewerExample.NativeControls;

public interface INativeDemoControl
{
    IPlatformHandle CreateControl();
}
