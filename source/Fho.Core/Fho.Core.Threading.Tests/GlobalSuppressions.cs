// This file is used by Code Analysis to maintain SuppressMessage
// attributes that are applied to this project.
// Project-level suppressions either have no target or are given
// a specific target and scoped to a namespace, type, member, etc.

using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage(
    category: "Maintainability",
    checkId: "CA1515:Consider making public types internal",
    Justification = "Tests must be public to be discovered by the test runner",
    Scope = "namespaceanddescendants",
    Target = "~N:Fho.Core.Threading.Tests")]
