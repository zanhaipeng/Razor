using System.IO;

namespace Microsoft.VisualStudio.ProjectSystem.Razor
{
    public interface IThingDoer
    {
        void DoTheNeedful(object project, string fullPath, Stream stream);
    }
}
