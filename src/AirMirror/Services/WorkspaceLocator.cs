using System.IO;

namespace AirMirror.Services;

public static class WorkspaceLocator
{
    public static string? FindWorkspaceRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "third_party", "UxPlay"))
                || File.Exists(Path.Combine(current.FullName, "src", "AirMirror", "AirMirror.csproj")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        var cwd = new DirectoryInfo(Environment.CurrentDirectory);
        while (cwd is not null)
        {
            if (Directory.Exists(Path.Combine(cwd.FullName, "third_party", "UxPlay")))
            {
                return cwd.FullName;
            }

            cwd = cwd.Parent;
        }

        return null;
    }
}
