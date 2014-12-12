using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.Framework.DesignTimeHost.Models.OutgoingMessages;

namespace DesignTimeHostDemo
{
    public class ProjectState
    {
        public ConcurrentDictionary<string, FrameworkState> ProjectsByFramework { get; private set; }

        public ProjectState()
        {
            ProjectsByFramework = new ConcurrentDictionary<string, FrameworkState>();
        }
    }

    public class FrameworkState
    {
        public ProjectId ProjectId { get; set; }

        public Dictionary<string, DocumentId> Documents { get; set; }

        public Dictionary<string, MetadataReference> FileReferences { get; set; }

        public Dictionary<string, MetadataReference> RawReferences { get; set; }

        public Dictionary<string, ProjectId> ProjectReferences { get; set; }

        public List<ProjectId> PendingProjectReferences { get; set; }

        public FrameworkState()
        {
            ProjectId = ProjectId.CreateNewId();
            Documents = new Dictionary<string, DocumentId>();
            FileReferences = new Dictionary<string, MetadataReference>();
            RawReferences = new Dictionary<string, MetadataReference>();
            ProjectReferences = new Dictionary<string, ProjectId>();
            PendingProjectReferences = new List<ProjectId>();
        }
    }
}
