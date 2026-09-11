namespace MeowSSH.TestHost;

/// <summary>Fixtures shared between the test host and the tests that drive it.</summary>
public static class TestHostDefaults
{
    /// <summary>
    /// A fixed recovery code so tests can type one in. Real vaults generate their
    /// own; this one only ever unlocks the fake.
    /// </summary>
    public const string RecoveryCode = "ABCD-EFGH-JKMN-PQRS-TVWX-YZ01-2345-6789";
}
