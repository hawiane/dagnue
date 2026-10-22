using Nexus.Core.Security;

namespace Nexus.Core.Tests;

public sealed class DatabaseCommandPolicyTests
{
    [Theory]
    [InlineData("SELECT * FROM LONQU.EMPLOYEE_INFO")]
    [InlineData("WITH E AS (SELECT 1 X FROM DUAL) SELECT * FROM E")]
    public void ReadPolicy_AllowsQueries(string sql) => DatabaseCommandPolicy.ValidateReadOnly(sql);

    [Theory]
    [InlineData("UPDATE LONQU.EMPLOYEE_INFO SET STATUS='A' WHERE ID=1")]
    [InlineData("DELETE FROM LONQU.TEST_ROWS WHERE ID=1")]
    [InlineData("INSERT INTO LONQU.TEST_ROWS(ID) VALUES(1)")]
    [InlineData("MERGE INTO LONQU.TEST_ROWS T USING DUAL ON (T.ID=1) WHEN MATCHED THEN UPDATE SET T.ID=1")]
    public void WritePolicy_AllowsGuardedCrud(string sql) => DatabaseCommandPolicy.ValidateControlledWrite(sql);

    [Theory]
    [InlineData("UPDATE LONQU.EMPLOYEE_INFO SET STATUS='A'")]
    [InlineData("DELETE FROM LONQU.EMPLOYEE_INFO")]
    [InlineData("DROP TABLE LONQU.EMPLOYEE_INFO")]
    [InlineData("GRANT DBA TO LONQU")]
    [InlineData("UPDATE LONQU.EMPLOYEE_INFO SET STATUS='A' WHERE ID=1; DELETE FROM X")]
    public void WritePolicy_BlocksDestructiveOrBroadSql(string sql) =>
        Assert.Throws<InvalidOperationException>(() => DatabaseCommandPolicy.ValidateControlledWrite(sql));
}
