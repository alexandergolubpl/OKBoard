namespace OKBoard;

/// <summary>Saves and restores the user's clipboard around our copy/paste operations.</summary>
internal sealed class ClipboardBackup
{
    private DataObject? _data;

    public static ClipboardBackup Save()
    {
        var b = new ClipboardBackup();
        try
        {
            var src = Clipboard.GetDataObject();
            if (src != null)
            {
                var d = new DataObject();
                foreach (var f in src.GetFormats(false))
                {
                    try
                    {
                        if (src.GetData(f, false) is { } v) d.SetData(f, false, v);
                    }
                    catch { /* some private formats can't be read back — skip them */ }
                }
                b._data = d;
            }
        }
        catch { }
        return b;
    }

    public void Restore()
    {
        try
        {
            if (_data != null && _data.GetFormats().Length > 0)
            {
                MarkPrivate(_data);
                Clipboard.SetDataObject(_data, true, 5, 30);
            }
            else Clipboard.Clear();
        }
        catch { }
    }

    private static void MarkPrivate(DataObject d)
    {
        d.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[4]));
        d.SetData("CanIncludeInClipboardHistory", new MemoryStream(new byte[4]));
        d.SetData("CanUploadToCloudClipboard", new MemoryStream(new byte[4]));
    }
}
