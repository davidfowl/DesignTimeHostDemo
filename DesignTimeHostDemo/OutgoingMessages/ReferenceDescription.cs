// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Collections.Generic;

namespace Microsoft.Framework.DesignTimeHost.Models.OutgoingMessages
{
    public class ReferenceDescription
    {
        public string Name { get; set; }

        public string Version { get; set; }

        public string Path { get; set; }

        public string Type { get; set; }

        public IEnumerable<ReferenceItem> Dependencies { get; set; }
    }
}