using System.Security.AccessControl;
using System.Security.Principal;

namespace DesktopNook.Core;
public static class StorageSecurity
{
    public static void Protect(string directory)
    {
        if(!OperatingSystem.IsWindows())return;
        var user=WindowsIdentity.GetCurrent().User??throw new IOException("无法确定数据目录所属用户。");
        var security=new DirectorySecurity();security.SetAccessRuleProtection(true,false);
        foreach(var sid in new[]{user,new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null)})
            security.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(security);
    }
}
