using System.IO;
using System.Security.Principal;
using System.Security.AccessControl;
using System.Text.Json;
using System.Xml.Linq;

namespace DesktopNook.App;

internal static class InfoBallAutomaticVerification
{
    internal static int Run(string root)
    {
        Directory.CreateDirectory(root);
        var checks = new List<string>();
        string? failure = null;
        try
        {
            byte[] helper = NativeInfoBallAutomatic.PayloadForVerification;
            Check(helper.Length > 100000 && helper[0] == 'M' && helper[1] == 'Z', "The release embeds a separate executable helper.");
            var xml = XDocument.Parse(NativeInfoBallAutomatic.DefinitionXmlForVerification());
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            var task = xml.Root!;
            Check(!task.Descendants(ns + "Trigger").Any() && !task.Element(ns + "Triggers")!.Elements().Any(),
                "The helper task has no logon, boot, timed or arbitrary event trigger.");
            var principal = task.Descendants(ns + "Principal").Single();
            string user = principal.Element(ns + "UserId")!.Value;
            if (!user.StartsWith("S-1-", StringComparison.Ordinal)) user = ((SecurityIdentifier)new NTAccount(user).Translate(typeof(SecurityIdentifier))).Value;
            Check(user == WindowsIdentity.GetCurrent().User!.Value && principal.Element(ns + "LogonType")!.Value == "InteractiveToken",
                "Only the current user's existing interactive session is used, with no stored password.");
            Check(principal.Element(ns + "RunLevel")!.Value == "HighestAvailable", "Only the dedicated task requests the required window permissions.");
            var actions = task.Element(ns + "Actions")!.Elements().ToArray();
            Check(actions.Length == 1 && actions[0].Name == ns + "Exec", "There is exactly one fixed executable action.");
            string command = actions[0].Element(ns + "Command")!.Value;
            Check(command.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(command) == "DesktopNook.InfoBallHelper.exe" && actions[0].Element(ns + "Arguments")!.Value == "--run",
                "The task invokes the protected helper directly, without PowerShell, a shell or caller-supplied arguments.");
            Check(task.Descendants(ns + "MultipleInstancesPolicy").Single().Value == "IgnoreNew", "Repeated requests cannot spawn concurrent positioning helpers.");
            Check(task.Descendants(ns + "ExecutionTimeLimit").Single().Value == "PT0S", "A long-running capsule session is not interrupted by a scheduler timeout.");
            var permissions = new RawSecurityDescriptor(NativeInfoBallAutomatic.TaskSecurityForVerification);
            var userAce = permissions.DiscretionaryAcl!.OfType<CommonAce>().Single(ace => ace.SecurityIdentifier == WindowsIdentity.GetCurrent().User);
            Check(permissions.Owner!.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) &&
                (userAce.AccessMask & (0x40000000 | 0x40000 | 0x80000 | 0x10000)) == 0,
                "The ordinary user cannot rewrite the privileged task, its owner or its access controls.");
            NativeInfoBallAutomatic.TryStart();
            bool refused = false;
            try { NativeInfoBallAutomatic.ConfigureAsync(true).GetAwaiter().GetResult(); }
            catch (InvalidOperationException) { refused = true; }
            Check(refused, "Sandbox configuration cannot register a privileged task.");
        }
        catch (Exception error) { failure = error.ToString(); }
        File.WriteAllText(Path.Combine(root, "info-ball-automatic-verification.json"), JsonSerializer.Serialize(new { passed = checks.Count, failure, checks }, new JsonSerializerOptions { WriteIndented = true }));
        return failure is null ? 0 : 1;

        void Check(bool condition, string text) { if (!condition) throw new IOException(text); checks.Add(text); }
    }
}
