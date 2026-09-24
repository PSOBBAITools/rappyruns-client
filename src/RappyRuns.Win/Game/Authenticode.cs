using RappyRuns.Core.Game;
using static RappyRuns.Win.Game.NativeMethods;

namespace RappyRuns.Win.Game;

/// <summary>
/// Authenticode verification of the PSOBB exe (win32.lisp:520
/// authenticode-verify, spec core §11.4). Revocation servers are never
/// contacted (cache-only URL retrieval) so this cannot block on the network.
/// A timestamp countersignature keeps an expired certificate Valid, which is
/// why the policy (<see cref="PsobbTables.SignatureTrusted"/>) pins the
/// signer's CN and not a thumbprint.
/// </summary>
public static unsafe class Authenticode
{
    /// <summary>WinVerifyTrust of the file: (Valid, leaf CN) / (Unsigned, null) / (Invalid, null).</summary>
    public static (SignatureStatus Status, string? Signer) Verify(string path)
    {
        fixed (char* pathPtr = path)
        {
            var fileInfo = new WintrustFileInfo
            {
                CbStruct = (uint)sizeof(WintrustFileInfo),
                FilePath = (nint)pathPtr,
            };
            var data = new WintrustData
            {
                CbStruct = (uint)sizeof(WintrustData),
                UiChoice = WtdUiNone,
                RevocationChecks = 0,
                UnionChoice = WtdChoiceFile,
                FileInfo = &fileInfo,
                StateAction = WtdStateActionVerify,
                ProvFlags = WtdCacheOnlyUrlRetrieval,
            };
            var action = WintrustActionGenericVerifyV2;
            var result = (uint)WinVerifyTrust(0, &action, &data);
            try
            {
                if (result == 0) return (SignatureStatus.Valid, SignerCommonName(data.StateData));
                if (result == TrustENoSignature) return (SignatureStatus.Unsigned, null);
                return (SignatureStatus.Invalid, null);
            }
            finally
            {
                // Release wintrust's verification state in all cases.
                data.StateAction = WtdStateActionClose;
                WinVerifyTrust(0, &action, &data);
            }
        }
    }

    /// <summary>win32.lisp:498 signer-common-name: subject CN of the leaf signing certificate, or null.</summary>
    private static string? SignerCommonName(nint stateData)
    {
        var provData = WTHelperProvDataFromStateData(stateData);
        if (provData == 0) return null;
        var signer = WTHelperGetProvSignerFromChain(provData, 0, false, 0);
        if (signer == null || signer->CsCertChain == 0 || signer->PasCertChain == null) return null;
        var cert = signer->PasCertChain[0].Cert;
        if (cert == 0) return null;
        const int maxChars = 256;
        var buffer = stackalloc char[maxChars];
        // The returned length includes the trailing NUL.
        var length = CertGetNameString(cert, CertNameSimpleDisplayType, 0, 0, buffer, maxChars);
        return length > 1 ? new string(buffer, 0, (int)length - 1) : null;
    }
}
