using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vistara.Persistence;
using Xunit;

namespace Vistara.IntegrationTests.Persistence;

/// <summary>
/// Pins the provider fault taxonomy the curation store depends on: a rejected
/// precondition must never look like an unavailable database, and an
/// unavailable database must never look like a settled conflict.
/// </summary>
public sealed class RelationalFaultClassifierTests
{
    [Fact]
    public void Optimistic_concurrency_failures_are_concurrency()
    {
        Assert.Equal(
            RelationalFaultKind.Concurrency,
            RelationalFaultClassifier.Classify(
                new DbUpdateConcurrencyException("fence lost")));
        Assert.Equal(
            RelationalFaultKind.Concurrency,
            RelationalFaultClassifier.Classify(
                new InvalidOperationException(
                    "wrapped",
                    new DbUpdateConcurrencyException("fence lost"))));
    }

    [Theory]
    [InlineData(19)]
    public void Sqlite_constraint_violations_are_preconditions(int errorCode) =>
        Assert.Equal(
            RelationalFaultKind.Precondition,
            RelationalFaultClassifier.Classify(
                new DbUpdateException(
                    "store failure",
                    new SqliteException("constraint failed", errorCode))));

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(10)]
    [InlineData(14)]
    public void Sqlite_contention_and_infrastructure_faults_are_unavailable(
        int errorCode) =>
        Assert.Equal(
            RelationalFaultKind.Unavailable,
            RelationalFaultClassifier.Classify(
                new DbUpdateException(
                    "store failure",
                    new SqliteException("statement failed", errorCode))));

    [Theory]
    [InlineData("23505")]
    [InlineData("23514")]
    [InlineData("23503")]
    [InlineData("23502")]
    [InlineData("23P01")]
    public void PostgreSql_integrity_violations_are_preconditions(string sqlState) =>
        Assert.Equal(
            RelationalFaultKind.Precondition,
            RelationalFaultClassifier.Classify(
                new DbUpdateException("store failure", Postgres(sqlState))));

    [Theory]
    [InlineData("40001")]
    [InlineData("40P01")]
    [InlineData("55P03")]
    [InlineData("57014")]
    [InlineData("57P03")]
    [InlineData("08006")]
    [InlineData("53300")]
    [InlineData("XX000")]
    public void PostgreSql_serialization_deadlock_and_provider_faults_are_unavailable(
        string sqlState) =>
        Assert.Equal(
            RelationalFaultKind.Unavailable,
            RelationalFaultClassifier.Classify(
                new DbUpdateException("store failure", Postgres(sqlState))));

    [Fact]
    public void Connection_and_timeout_faults_are_unavailable()
    {
        Assert.Equal(
            RelationalFaultKind.Unavailable,
            RelationalFaultClassifier.Classify(
                new DbUpdateException("store failure", new TimeoutException())));
        Assert.Equal(
            RelationalFaultKind.Unavailable,
            RelationalFaultClassifier.Classify(
                new DbUpdateException(
                    "store failure",
                    new IOException("the connection was reset"))));
    }

    [Fact]
    public void Unattributed_failures_stay_unknown()
    {
        Assert.Equal(
            RelationalFaultKind.Unknown,
            RelationalFaultClassifier.Classify(
                new DbUpdateException("store failure")));
        Assert.Equal(
            RelationalFaultKind.Unknown,
            RelationalFaultClassifier.Classify(
                new InvalidOperationException("not a database fault")));
    }

    [Fact]
    public void Contention_or_constraint_keeps_its_established_answer()
    {
        Assert.True(RelationalFaultClassifier.IsContentionOrConstraint(
            new DbUpdateException("store", new SqliteException("busy", 5))));
        Assert.True(RelationalFaultClassifier.IsContentionOrConstraint(
            new DbUpdateException("store", Postgres("23505"))));
        Assert.True(RelationalFaultClassifier.IsContentionOrConstraint(
            new DbUpdateException("store", Postgres("40001"))));
        Assert.False(RelationalFaultClassifier.IsContentionOrConstraint(
            new DbUpdateException("store", Postgres("08006"))));
        Assert.False(RelationalFaultClassifier.IsContentionOrConstraint(
            new InvalidOperationException("not a database fault")));
    }

    private static PostgresException Postgres(string sqlState) =>
        new PostgresException(
            "injected",
            "ERROR",
            "ERROR",
            sqlState);
}
