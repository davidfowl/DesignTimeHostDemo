using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;

namespace DesignTimeHostDemo
{
    public class ProjectState
    {
        public ConcurrentDictionary<string, ProjectId> WorkspaceProjects { get; private set; }

        public ProjectState()
        {
            WorkspaceProjects = new ConcurrentDictionary<string, ProjectId>();
        }
    }
}
