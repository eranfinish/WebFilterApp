using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

namespace WebFilter.Service.Guard;

public enum ProgramKind
{
    /// <summary>Not a web browser we know of. WFP still decides its network access.</summary>
    NotABrowser,

    /// <summary>A supported browser: at an installed path from the settings, with a valid Microsoft or Google signature.</summary>
    SupportedBrowser,

    /// <summary>A browser that is not supported (another browser, a copy, a renamed or unsigned build) — ProcessGuard ends it.</summary>
    UnsupportedBrowser,
}

/// <summary>
/// ProcessGuard's test (MVP-PLAN §4.2): a program in a child's session is a browser if its file name or its version
/// resource's OriginalFilename is a known browser (so renaming firefox.exe does not hide it). It is supported only at a
/// supported install path and with a valid Authenticode signature from the browser's vendor. Portable builds without
/// version info are not recognized here; WFP still keeps them off the network.
/// </summary>
[SupportedOSPlatform("windows")]
public static class BrowserClassifier
{
    private static readonly Dictionary<string, string?> KnownBrowsers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["msedge.exe"] = "Microsoft Corporation",
        ["chrome.exe"] = "Google LLC",
        ["firefox.exe"] = null, ["opera.exe"] = null, ["launcher.exe"] = null, ["brave.exe"] = null, ["vivaldi.exe"] = null,
        ["iexplore.exe"] = null, ["waterfox.exe"] = null, ["librewolf.exe"] = null, ["palemoon.exe"] = null, ["seamonkey.exe"] = null,
        ["browser.exe"] = null, ["yandex.exe"] = null, ["maxthon.exe"] = null, ["arc.exe"] = null, ["thorium.exe"] = null,
    };

    public static ProgramKind Classify(string path, IReadOnlyCollection<string> supportedBrowsers)
    {
        var fileName = Path.GetFileName(path);
        string? original = null;
        try { original = FileVersionInfo.GetVersionInfo(path).OriginalFilename?.Trim(); }
        catch (FileNotFoundException) { }

        var name = original is { Length: > 0 } && KnownBrowsers.ContainsKey(original) ? original
            : KnownBrowsers.ContainsKey(fileName) ? fileName
            : null;
        // Opera's launcher.exe is only a browser when its version info says Opera.
        if (name is null || (name.Equals("launcher.exe", StringComparison.OrdinalIgnoreCase) && !IsOpera(path))) return ProgramKind.NotABrowser;

        var vendor = KnownBrowsers[name];
        var supportedPath = supportedBrowsers.Any(b => string.Equals(Path.GetFullPath(b), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
        return vendor is not null && supportedPath && Authenticode.SignerOrganization(path) == vendor
            ? ProgramKind.SupportedBrowser
            : ProgramKind.UnsupportedBrowser;
    }

    private static bool IsOpera(string path)
    {
        try { return FileVersionInfo.GetVersionInfo(path).ProductName?.Contains("Opera", StringComparison.OrdinalIgnoreCase) == true; }
        catch (FileNotFoundException) { return false; }
    }
}

/// <summary>Authenticode verification with WinVerifyTrust (chain and signature; revocation is not checked offline).</summary>
[SupportedOSPlatform("windows")]
public static class Authenticode
{
    /// <summary>The O= of a valid embedded signature's signer, or null if the file is unsigned or the signature is invalid.</summary>
    public static string? SignerOrganization(string path)
    {
        if (!IsValid(path)) return null;
        try
        {
#pragma warning disable SYSLIB0057 // Reading the signer of a signed file; there is no X509CertificateLoader equivalent.
            using var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return Organization(signer.SubjectName);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    private static string? Organization(X500DistinguishedName name)
    {
        foreach (var rdn in name.EnumerateRelativeDistinguishedNames())
            if (rdn.GetSingleElementType().Value == "2.5.4.10") return rdn.GetSingleElementValue();
        return null;
    }

    private static bool IsValid(string path)
    {
        var file = new WintrustFileInfo { cbStruct = (uint)Marshal.SizeOf<WintrustFileInfo>(), pcwszFilePath = path };
        var filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(file, filePtr, false);
            var data = new WintrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WintrustData>(),
                dwUIChoice = 2,          // WTD_UI_NONE
                fdwRevocationChecks = 0, // WTD_REVOKE_NONE
                dwUnionChoice = 1,       // WTD_CHOICE_FILE
                pFile = filePtr,
                dwStateAction = 0,
                dwProvFlags = 0x1000,    // WTD_CACHE_ONLY_URL_RETRIEVAL: never go to the network
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            return WinVerifyTrust(new IntPtr(-1), ref action, ref data) == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(filePtr);
        }
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WintrustData pWVTData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WintrustFileInfo
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
