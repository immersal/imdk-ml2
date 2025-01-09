using System.Collections;
using System.Collections.Generic;
using Immersal;
using UnityEditor;

#if UNITY_EDITOR && IMMERSAL_MAGIC_LEAP_ENABLED && IMMERSAL_ISSUE_PROVIDERS
internal class MagicLeapProjectIssueProvider : IImmersalProjectIssueProvider
{
    public string Name => "Magic Leap 2";
    public bool DisableDefaultIssues => true;
    public bool Enabled { get; set; } = true;
    
    public IEnumerable<ImmersalProjectIssue> Issues => new []
    {
        new ImmersalProjectIssue()
        {
            Message = () => "Please refer to Magic Leap documentation for project validation",
            Check = () => false,
            Fix = null,
            Error = false,
        },
    };
    
    [InitializeOnLoadMethod]
    private static void RegisterProvider()
    {
        ImmersalProjectValidation.RegisterIssueProvider(new MagicLeapProjectIssueProvider());
    }
}
#endif