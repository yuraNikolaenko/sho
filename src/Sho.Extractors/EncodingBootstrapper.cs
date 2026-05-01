using System.Runtime.CompilerServices;
using System.Text;

namespace Sho.Extractors;

internal static class EncodingBootstrapper
{
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Init()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }
}
