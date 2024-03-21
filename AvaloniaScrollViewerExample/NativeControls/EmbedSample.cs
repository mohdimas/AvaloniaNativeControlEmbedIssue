using Avalonia.Controls;
using Avalonia.Platform;

namespace AvaloniaScrollViewerExample.NativeControls;

public class EmbedSample : NativeControlHost
{
    public static INativeDemoControl? Implementation { get; set; } = new EmbedSampleMac();

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        return Implementation?.CreateControl() ?? base.CreateNativeControlCore(parent);
    }
}
