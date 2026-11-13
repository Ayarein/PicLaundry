using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace PicLaundry;

internal sealed partial class Shelf
{
    private readonly DispatcherTimer clickTimer = new();
    private Entry? pendingClick;
    private long clickDeadline;
    private bool secondPress;
    private ImagePreview? imagePreview;

    private void InitializeClicks()
    {
        clickTimer.Tick += (_, _) => CommitClick();
        Closed += (_, _) =>
        {
            CancelClick();
            imagePreview?.Close();
        };
    }

    private void BeginClick(Guid id)
    {
        if (pendingClick?.Id == id && Environment.TickCount64 <= clickDeadline)
        {
            secondPress = true;
            clickTimer.Stop();
        }
        else CommitClick();
    }

    private void ReleaseClick(Entry entry)
    {
        if (secondPress && pendingClick?.Id == entry.Id)
        {
            CancelClick();
            OpenPreview(entry);
            return;
        }

        int delay = Forms.SystemInformation.DoubleClickTime;
        pendingClick = entry;
        clickDeadline = Environment.TickCount64 + delay;
        clickTimer.Interval = TimeSpan.FromMilliseconds(delay);
        clickTimer.Start();
    }

    private void FinishInterruptedClick()
    {
        if (secondPress) CancelClick();
    }

    private void CancelClick()
    {
        clickTimer.Stop();
        pendingClick = null;
        secondPress = false;
    }

    private void CommitClick()
    {
        var entry = pendingClick;
        CancelClick();
        if (entry != null) copy(entry);
    }

    private void OpenPreview(Entry entry)
    {
        ImagePreview viewer;
        try
        {
            viewer = new ImagePreview(History.Decode(entry.Path!), Path.GetFileName(entry.Path!));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException or COMException or InvalidOperationException)
        {
            MessageBox.Show("无法打开原图。文件可能已移走或无法读取。", "PicLaundry", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        imagePreview?.Close();
        imagePreview = viewer;
        viewer.Closed += (_, _) => imagePreview = null;
        viewer.Show();
    }
}
