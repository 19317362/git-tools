﻿using Gitscc;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GitScc.DataServices
{
    public class RepositoryGraph
    {
        private const int CommitsToLoad = 200;
        private const string LogFormat = "--pretty=format:%H%n%P%n%ar%n%an%n%ae%n%ci%n%T%n%s%n%b";

        private string workingDirectory;

        private IList<Commit> commits;
        private IList<Ref> refs;
        private IList<GraphNode> nodes;
        private IList<GraphLink> links;
        private bool isSimplified;

        public RepositoryGraph(string repository)
        {
            this.workingDirectory = repository;
        }

        public IEnumerable<Commit> Commits
        {
            get
            {
                if (commits == null)
                {
                    var result = GitBash.Run(string.Format("log -n {0} --date-order --all --boundary -z {1} HEAD",
                        CommitsToLoad, LogFormat),
                        this.workingDirectory);

                    if (result.HasError || string.IsNullOrEmpty(result.Output) || result.Output.StartsWith("fatal:"))
                    {
                        commits = new List<Commit>();
                        return commits;
                    }

                    var logs = result.Output.Split('\0');
                    commits = logs.Select(log => ParseCommit(log)).ToList();
                    commits.ToList().ForEach(
                        commit => commit.ChildIds =
                                  commits.Where(c => c.ParentIds.Contains(commit.Id))
                                         .Select(c => c.Id).ToList());
                }

                return commits;
            }
        }

        private Commit ParseCommit(string log)
        {
            string[] ss = log.Split('\n');
            return new Commit
            {
                Id = ss[0],
                ParentIds = ss[1].Split(' '),
                AuthorDateRelative = ss[2],
                AuthorName = ss[3],
                AuthorEmail = ss[4],
                AuthorDate = DateTime.Parse(ss[5]),
                TreeId = ss[6],
                Subject = ss[7],
                Message = ss[7] + (ss.Length <= 8 ? "" : "\n\n" + string.Join("\n", ss, 8, ss.Length - 8))
            };
        }

        public IList<Ref> Refs
        {
            get
            {
                if (refs == null)
                {
                    var branch = "";

                    var result = GitBash.Run("rev-parse --abbrev-ref HEAD", this.workingDirectory);
                    if (!result.HasError && !result.Output.Contains("fatal:"))
                    {
                        branch = "refs/heads/" + result.Output.Trim();
                    }

                    result = GitBash.Run("show-ref --head --dereference", this.workingDirectory);
                    if (!result.HasError && !result.Output.Contains("fatal:"))
                    {
                        refs = (from t in result.Output.Split('\n')
                                where !string.IsNullOrWhiteSpace(t)
                                select new Ref
                                {
                                    Id = t.Substring(0, 40),
                                    RefName = t.Substring(41),
                                    IsHead = t.Substring(41).Equals(branch) ? "*" : ""
                                }).ToList();
                    }
                }
                return refs;
            }
        }

        public IList<GraphNode> Nodes
        {
            get
            {
                if (nodes == null) GenerateGraph();
                return nodes;
            }
        }

        public IEnumerable<GraphLink> Links
        {
            get
            {
                if (links == null) GenerateGraph();
                return links;
            }
        }

        private void GenerateGraph()
        {
            GenerateGraph(Commits);
            if (IsSimplified)
            {
                GenerateGraph(GetSimplifiedCommits());
            }
        }

        private void GenerateGraph(IEnumerable<Commit> commits)
        {
            nodes = new List<GraphNode>();
            links = new List<GraphLink>();
            var lanes = new List<string>();

            int i = 0;

            foreach (var commit in commits)
            {
                var id = commit.Id;

                var refs = from r in this.Refs
                           where r.Id == id
                           select r;

                var children = (from c in commits
                                where c.ParentIds.Contains(id)
                                select c).ToList();

                var parents = (from c in commits
                               where c.ChildIds.Contains(id)
                               select c).ToList();


                var idx = lanes.Count - 1;
                while (idx > 0)
                {
                    if (lanes[idx] == id || children.Any(c => lanes[idx] == c.Id)) lanes.RemoveAt(idx);
                    else break;
                    idx = lanes.Count - 1;
                }

                var lane = lanes.IndexOf(id);

                if (lane < 0)
                {
                    lanes.Add(id);
                    lane = lanes.Count - 1;
                }

                int m = parents.Count() - 1;
                for (int n = 0; n <= m; n++)
                {
                    var parentId = parents[n].Id;
                    if (n == m || lanes.IndexOf(parentId) >= 0)
                        lanes[lane] = parentId;
                    else
                        lanes.Add(parentId);
                }

                var node = new GraphNode
                {
                    X = lane,
                    Y = i++,
                    Id = id,
                    Subject = commit.Subject,
                    Message = commit.Message,
                    AuthorName = commit.AuthorName,
                    AuthorDateRelative = commit.AuthorDateRelative,
                    Refs = refs.ToArray(),
                };

                nodes.Add(node);

                foreach (var ch in children)
                {
                    var cnode = (from n in nodes
                                 where n.Id == ch.Id
                                 select n).FirstOrDefault();

                    if (cnode != null)
                    {
                        links.Add(new GraphLink
                        {
                            X1 = cnode.X,
                            Y1 = cnode.Y,
                            X2 = node.X,
                            Y2 = node.Y,
                            Id = id
                        });
                    }
                }

            }
        }

        private IEnumerable<Commit> GetSimplifiedCommits()
        {
            foreach (var commit in Commits)
            {
                if (commit.ParentIds.Count() == 1 && commit.ChildIds.Count() == 1 && !this.Refs.Any(r => r.Id == commit.Id))
                {
                    var cid = commit.ChildIds[0];
                    var pid = commit.ParentIds[0];

                    var parent = Commits.Where(c => c.Id == pid).FirstOrDefault();
                    var child = Commits.Where(c => c.Id == cid).FirstOrDefault();

                    if (parent != null && child != null)
                    {
                        int x1 = GetLane(parent.Id);
                        int x2 = GetLane(commit.Id);
                        int x3 = GetLane(child.Id);

                        if (x1 == x2 && x2 == x3)
                        {
                            commit.deleted = true;
                            parent.ChildIds[parent.ChildIds.IndexOf(commit.Id)] = cid;
                            child.ParentIds[child.ParentIds.IndexOf(commit.Id)] = pid;
                        }
                    }
                }
            }

            return commits.Where(c => !c.deleted);
        }

        private int GetLane(string id)
        {
            return Nodes.Where(n => n.Id == id).Select(n => n.X).FirstOrDefault();
        }

        public bool IsSimplified
        {
            get { return isSimplified; }
            set { isSimplified = value; commits = null; nodes = null; links = null; }
        }

        public Commit GetCommit(string commitId)
        {
            try
            {
                var result = GitBash.Run(string.Format("log -1 {0} {1}", LogFormat, commitId),
                    this.workingDirectory);

                return ParseCommit(result.Output);
            }
            catch (Exception ex)
            {
                Log.WriteLine("Repository.GetCommit: {0} \r\n{1}", commitId, ex.ToString());
            }
            return null;
        }

        public GitTreeObject GetTree(string commitId)
        {
            var commit = GetCommit(commitId);
            if (commit == null) return null;

            return new GitTreeObject
            {
                Id = commitId,
                Name = "",
                FullName = "",
                Type = "tree",
                IsExpanded = true,
                Repository = this.workingDirectory,
            };
        }

        public IEnumerable<Change> GetChanges(string commitId)
        {
            return GetChanges(commitId + "~1", commitId);
        }

        public IEnumerable<Change> GetChanges(string fromCommitId, string toCommitId)
        {
            var changes = new List<Change>();

            try
            {
                var result = GitBash.Run(string.Format("diff -M -C --name-status -z {0} {1}", fromCommitId, toCommitId), this.workingDirectory);

                if (!string.IsNullOrWhiteSpace(result.Output))
                {
                    //from gitextensions GitCommandHelper.cs
                    var nl = new char[] { '\n', '\r' };
                    string trimmedStatus = result.Output.Trim(nl);
                    int lastNewLinePos = trimmedStatus.LastIndexOfAny(nl);
                    if (lastNewLinePos > 0)
                    {
                        int ind = trimmedStatus.LastIndexOf('\0');
                        if (ind < lastNewLinePos) //Warning at end
                        {
                            lastNewLinePos = trimmedStatus.IndexOfAny(nl, ind >= 0 ? ind : 0);
                            trimmedStatus = trimmedStatus.Substring(0, lastNewLinePos).Trim(nl);
                        }
                        else //Warning at beginning
                            trimmedStatus = trimmedStatus.Substring(lastNewLinePos).Trim(nl);
                    }

                    var files = trimmedStatus.Split(new char[] { '\0' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int n = 0; n < files.Length; n++)
                    {
                        string status = files[n];
                        var fileName = string.Empty;
                        var change = ParseStaus(status);

                        switch (change)
                        {
                            case ChangeType.Renamed:
                            case ChangeType.Copied:
                                fileName = files[n + 2];
                                n++; n++;
                                break;
                            case ChangeType.Unknown: 
                                continue;
                            default: 
                            
                                fileName = files[n + 1];
                                n++;
                                break;
                        }

                        changes.Add(new Change
                        {
                            ChangeType = change,
                            Name = fileName.Trim()
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Log.WriteLine("Repository.GetChanges: {0} - {1}\r\n{2}", fromCommitId, toCommitId, ex.ToString());
            }

            return changes;
        }

        private ChangeType ParseStaus(string status)
        {
            if(string.IsNullOrEmpty(status)) return ChangeType.Unknown;

            char x = status[0];
            switch (x)
            {
                case 'A':
                    return ChangeType.Added;
                case 'C':
                    return ChangeType.Copied;
                case 'D':
                    return ChangeType.Deleted;
                case 'M':
                    return ChangeType.Modified;
                case 'R':
                    return ChangeType.Renamed;
                case 'T':
                    return ChangeType.TypeChanged;
                case 'U':
                    return ChangeType.Unmerged;
            }
            return ChangeType.Unknown;
        }

        public byte[] GetFileContent(string commitId, string fileName)
        {
            try
            {
                var tmpFileName = GetFile(commitId, fileName);
                var content = File.ReadAllBytes(tmpFileName);
                if (File.Exists(tmpFileName)) File.Delete(tmpFileName);
                return content;
            }
            catch (Exception ex)
            {
                Log.WriteLine("Repository.GetFileContent: {0} - {1}\r\n{2}", commitId, fileName, ex.ToString());
            }

            return null;
        }

        public string GetFile(string commitId, string fileName)
        {
            var tmpFileName = Path.GetTempFileName();
            tmpFileName = Path.ChangeExtension(tmpFileName, Path.GetExtension(fileName));
            try
            {
                GitBash.RunCmd(string.Format("cat-file blob {0}:./{1} > {2}", commitId, fileName, tmpFileName),
                    this.workingDirectory);
            }
            catch (Exception ex)
            {
                Log.WriteLine("Repository.GetFile: {0} - {1}\r\n{2}", commitId, fileName, ex.ToString());
            }
            return tmpFileName;
        }
    }
}