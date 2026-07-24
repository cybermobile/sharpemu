// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Xunit;

namespace SharpEmu.Libs.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class App0EnvironmentCollection
{
    public const string Name = "App0 environment";
}
