﻿//
// Copyright (c) 2017 The nanoFramework project contributors
// Portions Copyright (c) Microsoft Corporation.  All rights reserved.
// See LICENSE file in the project root for full license information.
//

using System.Collections.Generic;

namespace nanoFramework.Tools.Debugger
{
    public interface INanoFrameworkDeviceInfo
    {
        bool Valid { get; }
        System.Version HalBuildVersion { get; }
        string HalBuildInfo { get; }
        byte OEM { get; }
        byte Model { get; }
        ushort SKU { get; }
        string TargetName { get; }
        string Platform { get; }
        string ModuleSerialNumber { get; }
        string SystemSerialNumber { get; }
        System.Version ClrBuildVersion { get; }
        string ClrBuildInfo { get; }
        System.Version TargetFrameworkVersion { get; }
        System.Version SolutionBuildVersion { get; }
        string SolutionBuildInfo { get; }
        IAppDomainInfo[] AppDomains { get; }
        IAssemblyInfo[] Assemblies { get; }
        List<CLRCapabilities.NativeAssemblyProperties> NativeAssemblies { get; }
        byte PlatformCapabilities { get; }
        byte TargetCapabilities { get; }
    }
}
