using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using DeepSeek_v4_for_VisualStudio.Utils;

namespace DeepSeek_v4_for_VisualStudio.Models
{
    /// <summary>
    /// 对话树节点 — 通用节点，不区分 user/assistant。
    /// 每个节点包含一条消息（用户或助手），Children 列表表示该位置的所有替代后续（分叉）。
    /// 
    /// 树结构约定：
    /// - Root 的子节点必为用户消息
    /// - 用户消息的子节点必为助手消息（重试在此分叉）
    /// - 助手消息的子节点必为用户消息（编辑在此分叉）
    /// - Children 列表 = 该位置的替代选择
    /// </summary>
    public class ConvNode
    {
        /// <summary>唯一节点 ID (GUID，不含连字符)</summary>
        public string Id { get; set; }

        /// <summary>该节点的消息（user 或 assistant）</summary>
        public ChatMessage Message { get; set; }

        /// <summary>父节点（Root 为 null）</summary>
        [JsonIgnore]
        public ConvNode? Parent { get; set; }

        /// <summary>子节点列表 = 该位置的所有替代后续（分叉分支）</summary>
        public List<ConvNode> Children { get; set; } = new();

        /// <summary>在 Parent.Children 中的索引（0-based），运行时计算</summary>
        [JsonIgnore]
        public int SiblingIndex => Parent?.Children.IndexOf(this) ?? 0;

        /// <summary>兄弟姐妹总数（Parent.Children.Count），运行时计算</summary>
        [JsonIgnore]
        public int SiblingCount => Parent?.Children.Count ?? 1;

        /// <summary>是否为用户消息</summary>
        [JsonIgnore]
        public bool IsUserMessage => Message?.Role == "user";

        /// <summary>是否为助手消息</summary>
        [JsonIgnore]
        public bool IsAssistantMessage => Message?.Role == "assistant";

        /// <summary>
        /// 从当前节点沿第一个子节点走到叶子，返回叶子节点。
        /// 用于分支切换后自动走到该分支的最新位置。
        /// </summary>
        public ConvNode GetLeaf()
        {
            var node = this;
            while (node.Children.Count > 0)
                node = node.Children[0];
            return node;
        }

        public override string ToString()
            => $"[{Id}] {(IsUserMessage ? "U" : IsAssistantMessage ? "A" : "?")}: {(Message?.Content ?? "(null)")}".Truncate(60);
    }

    /// <summary>
    /// 对话树管理器 — 管理整个对话树结构、分支导航、消息展平。
    /// </summary>
    public class ConversationTree
    {
        /// <summary>虚拟根节点（非用户消息，仅作为树的起点）</summary>
        public ConvNode Root { get; private set; }

        /// <summary>当前活跃路径的叶子节点</summary>
        public ConvNode ActiveLeaf { get; private set; }

        /// <summary>树中所有节点的扁平字典（按 Id 索引，用于快速查找）</summary>
        private readonly Dictionary<string, ConvNode> _nodeIndex = new(StringComparer.Ordinal);

        public ConversationTree()
        {
            Root = new ConvNode
            {
                Id = "root",
                Message = new ChatMessage { Role = "system", Content = "(virtual root)" },
            };
            _nodeIndex[Root.Id] = Root;
            ActiveLeaf = Root;
        }

        #region Node Management

        /// <summary>
        /// 注册节点到索引字典。
        /// </summary>
        private void RegisterNode(ConvNode node)
        {
            if (!string.IsNullOrEmpty(node.Id))
                _nodeIndex[node.Id] = node;
        }

        /// <summary>
        /// 按 Id 查找节点。
        /// </summary>
        public ConvNode? FindNode(string nodeId)
        {
            _nodeIndex.TryGetValue(nodeId, out var node);
            return node;
        }

        /// <summary>
        /// 生成新的节点 ID。
        /// </summary>
        private static string NewNodeId() => Guid.NewGuid().ToString("N");

        #endregion

        #region Tree Operations

        /// <summary>
        /// 在活跃分支末尾追加新消息（首次发送，非分叉）。
        /// </summary>
        /// <param name="message">要追加的消息（user 或 assistant）</param>
        /// <returns>新创建的节点</returns>
        public ConvNode AddChildMessage(ChatMessage message)
        {
            var parent = ActiveLeaf;

            var node = new ConvNode
            {
                Id = NewNodeId(),
                Message = message,
                Parent = parent,
            };
            message.NodeId = node.Id;

            parent.Children.Add(node);
            RegisterNode(node);
            ActiveLeaf = node;
            RefreshSiblingMetadata(parent);
            return node;
        }

        /// <summary>
        /// 在指定节点处创建分叉 — 在 existingNode 的父节点下创建新兄弟节点，
        /// 并自动切换到新分支。
        /// </summary>
        /// <param name="existingNode">当前所在的分支节点（将被保留，新节点作为其兄弟）</param>
        /// <param name="newMessage">新分支的首条消息</param>
        /// <param name="forkReason">"edit" 或 "retry"</param>
        /// <returns>新创建的节点（已是 ActiveLeaf 所在分支的根）</returns>
        public ConvNode ForkAt(ConvNode existingNode, ChatMessage newMessage, string forkReason)
        {
            var parent = existingNode.Parent ?? Root;

            newMessage.ForkReason = forkReason;
            newMessage.NodeId = string.Empty; // overwritten below

            var newNode = new ConvNode
            {
                Id = NewNodeId(),
                Message = newMessage,
                Parent = parent,
            };
            newMessage.NodeId = newNode.Id;

            // 插入到 existingNode 之后（保持顺序）
            int existingIndex = parent.Children.IndexOf(existingNode);
            if (existingIndex >= 0)
                parent.Children.Insert(existingIndex + 1, newNode);
            else
                parent.Children.Add(newNode);

            RegisterNode(newNode);
            RefreshSiblingMetadata(parent);

            // 切换到新分支的叶子（新节点无子节点，所以就是它自己）
            ActiveLeaf = newNode;
            return newNode;
        }

        /// <summary>
        /// 原地替换节点消息（不产生分支，用于 EditAgent 的编辑/重试）。
        /// 直接修改 existingNode 的消息内容，并移除其所有子节点（剪枝子树），
        /// 使后续对话在替换后的节点上继续。
        /// </summary>
        /// <param name="existingNode">要原地替换的节点</param>
        /// <param name="newMessage">新消息（直接赋给 existingNode.Message）</param>
        /// <returns>替换后的节点（即 existingNode）</returns>
        public ConvNode ReplaceInPlace(ConvNode existingNode, ChatMessage newMessage)
        {
            // ── 剪枝：移除所有子节点及其后代 ──
            foreach (var child in existingNode.Children)
                UnregisterSubtree(child);
            existingNode.Children.Clear();

            // ── 原地替换消息 ──
            newMessage.NodeId = existingNode.Id;
            newMessage.ForkReason = null; // 不显示分支导航
            newMessage.SiblingIndex = 1;
            newMessage.SiblingCount = 1;
            existingNode.Message = newMessage;

            // ── 更新父节点的兄弟元数据（existingNode 的兄弟数可能因之前的分叉而变化）──
            var parent = existingNode.Parent;
            if (parent != null)
                RefreshSiblingMetadata(parent);

            // ── 切换到替换后的节点 ──
            ActiveLeaf = existingNode;
            return existingNode;
        }

        /// <summary>
        /// 从索引字典中递归注销节点及其所有后代。
        /// </summary>
        private void UnregisterSubtree(ConvNode node)
        {
            _nodeIndex.Remove(node.Id);
            foreach (var child in node.Children)
                UnregisterSubtree(child);
        }

        /// <summary>
        /// 从树中移除指定节点及其所有后代，并将 ActiveLeaf 设为该节点的父节点。
        /// 用于 EditAgent 重试：移除旧助手回复，后续 Regenerate 会在父节点下创建新回复。
        /// </summary>
        /// <param name="node">要移除的节点（不能是 Root）</param>
        /// <returns>父节点（新的 ActiveLeaf）</returns>
        public ConvNode RemoveNodeFromTree(ConvNode node)
        {
            var parent = node.Parent ?? Root;

            // ── 从父节点的 Children 中移除 ──
            parent.Children.Remove(node);

            // ── 注销该节点及其所有后代 ──
            UnregisterSubtree(node);

            // ── 更新兄弟元数据 ──
            RefreshSiblingMetadata(parent);

            // ── 将活跃叶子设为父节点，后续 AddChildMessage 会在父节点下创建新子节点 ──
            ActiveLeaf = parent;
            return parent;
        }

        /// <summary>
        /// 在兄弟节点间切换分支。
        /// </summary>
        /// <param name="currentNode">当前显示的分支节点（需有兄弟）</param>
        /// <param name="direction">-1（上一个兄弟）或 +1（下一个兄弟）</param>
        /// <returns>切换后新分支的叶子节点，如果无切换则返回 null</returns>
        public ConvNode? NavigateSibling(ConvNode currentNode, int direction)
        {
            var parent = currentNode.Parent;
            if (parent == null || parent.Children.Count <= 1)
                return null;

            int currentIdx = parent.Children.IndexOf(currentNode);
            if (currentIdx < 0)
                return null;

            int newIdx = currentIdx + direction;
            if (newIdx < 0 || newIdx >= parent.Children.Count)
                return null; // 边界，无法切换

            var sibling = parent.Children[newIdx];
            ActiveLeaf = sibling.GetLeaf();
            RefreshSiblingMetadata(parent);
            return ActiveLeaf;
        }

        /// <summary>
        /// 刷新父节点下所有子节点的兄弟元数据（SiblingIndex/SiblingCount）。
        /// </summary>
        private void RefreshSiblingMetadata(ConvNode parent)
        {
            int count = parent.Children.Count;
            for (int i = 0; i < count; i++)
            {
                var child = parent.Children[i];
                child.Message.SiblingIndex = i + 1; // 1-based for display
                child.Message.SiblingCount = count;
            }
        }

        #endregion

        #region Path & Display

        /// <summary>
        /// 获取从 Root 到 ActiveLeaf 的完整路径。
        /// </summary>
        public List<ConvNode> GetActivePath()
        {
            var path = new List<ConvNode>();
            var node = ActiveLeaf;
            while (node != null && node != Root)
            {
                path.Insert(0, node);
                node = node.Parent;
            }
            return path;
        }

        /// <summary>
        /// 将活跃路径展平为渲染用的消息列表。
        /// 跳过 Root，按顺序提取每个节点的 Message。
        /// </summary>
        public List<ChatMessage> GetActiveMessages()
        {
            var messages = new List<ChatMessage>();
            var path = GetActivePath();
            foreach (var node in path)
            {
                if (node.Message != null)
                    messages.Add(node.Message);
            }
            return messages;
        }

        /// <summary>
        /// 获取所有从 Root 到叶子的完整路径（用于 token 统计）。
        /// </summary>
        public List<List<ConvNode>> GetAllLeafPaths()
        {
            var allPaths = new List<List<ConvNode>>();
            CollectLeafPaths(Root, new List<ConvNode>(), allPaths);
            return allPaths;
        }

        private void CollectLeafPaths(ConvNode node, List<ConvNode> currentPath, List<List<ConvNode>> allPaths)
        {
            // 跳过 Root
            if (node != Root)
                currentPath.Add(node);

            if (node.Children.Count == 0)
            {
                // 到达叶子
                if (currentPath.Count > 0)
                    allPaths.Add(new List<ConvNode>(currentPath));
            }
            else
            {
                foreach (var child in node.Children)
                    CollectLeafPaths(child, currentPath, allPaths);
            }

            if (node != Root)
                currentPath.RemoveAt(currentPath.Count - 1);
        }

        /// <summary>
        /// 统计树中总节点数（不含 Root）。
        /// </summary>
        public int TotalNodeCount
        {
            get
            {
                int count = 0;
                CountNodes(Root, ref count);
                return count;
            }
        }

        private void CountNodes(ConvNode node, ref int count)
        {
            if (node != Root) count++;
            foreach (var child in node.Children)
                CountNodes(child, ref count);
        }

        /// <summary>
        /// 统计分支总数（Root.Children.Count = 顶级用户消息数）。
        /// 注意：这里返回的是顶级分支数（不同对话开头），
        /// 分叉点处的分支数通过 ConvNode.SiblingCount 获取。
        /// </summary>
        public int BranchCount => Root.Children.Count;

        #endregion

        #region Token Calculation

        /// <summary>
        /// 计算所有完整分支路径中 token 数的最大值。
        /// </summary>
        /// <param name="tokenEstimator">token 估算函数（输入字符串，返回 token 数）</param>
        public int CalculateMaxPathTokens(Func<string, int> tokenEstimator)
        {
            var allPaths = GetAllLeafPaths();
            if (allPaths.Count == 0) return 0;

            int maxTokens = 0;
            foreach (var path in allPaths)
            {
                int pathTokens = 0;
                foreach (var node in path)
                {
                    if (node.Message != null)
                    {
                        if (!string.IsNullOrEmpty(node.Message.Content))
                            pathTokens += tokenEstimator(node.Message.Content);
                        if (!string.IsNullOrEmpty(node.Message.ReasoningContent))
                            pathTokens += tokenEstimator(node.Message.ReasoningContent);
                    }
                }
                if (pathTokens > maxTokens)
                    maxTokens = pathTokens;
            }
            return maxTokens;
        }

        /// <summary>
        /// 计算当前活跃路径的 token 数。
        /// </summary>
        public int CalculateActivePathTokens(Func<string, int> tokenEstimator)
        {
            var path = GetActivePath();
            int tokens = 0;
            foreach (var node in path)
            {
                if (node.Message != null)
                {
                    if (!string.IsNullOrEmpty(node.Message.Content))
                        tokens += tokenEstimator(node.Message.Content);
                    if (!string.IsNullOrEmpty(node.Message.ReasoningContent))
                        tokens += tokenEstimator(node.Message.ReasoningContent);
                }
            }
            return tokens;
        }

        #endregion

        #region Serialization

        /// <summary>
        /// 序列化为 TreePersistenceData（用于 JSON 持久化）。
        /// </summary>
        public TreePersistenceData Serialize()
        {
            var data = new TreePersistenceData
            {
                Version = 2,
                ActiveLeafId = ActiveLeaf.Id,
                Nodes = new List<TreeNodeData>(),
            };

            // BFS/DFS 收集所有节点
            var visited = new HashSet<string>();
            CollectNodes(Root, data.Nodes, visited);

            return data;
        }

        private void CollectNodes(ConvNode node, List<TreeNodeData> list, HashSet<string> visited)
        {
            if (!visited.Add(node.Id)) return; // 防止循环引用

            var nodeData = new TreeNodeData
            {
                Id = node.Id,
                ParentId = node.Parent?.Id,
                Message = node.Message != Root.Message ? node.Message : null, // Root 不保存虚拟消息
                ChildrenIds = node.Children.Select(c => c.Id).ToList(),
            };
            list.Add(nodeData);

            foreach (var child in node.Children)
                CollectNodes(child, list, visited);
        }

        /// <summary>
        /// 从 TreePersistenceData 反序列化。
        /// </summary>
        public static ConversationTree Deserialize(TreePersistenceData data)
        {
            var tree = new ConversationTree();
            if (data.Nodes == null || data.Nodes.Count == 0)
                return tree;

            // 第一遍：创建所有节点
            var nodeMap = new Dictionary<string, ConvNode>(StringComparer.Ordinal);
            foreach (var nd in data.Nodes)
            {
                var node = new ConvNode
                {
                    Id = nd.Id,
                    Message = nd.Message ?? new ChatMessage { Role = "system", Content = "(virtual root)" },
                };
                nodeMap[nd.Id] = node;
                tree._nodeIndex[nd.Id] = node;
            }

            // 第二遍：建立父子关系
            foreach (var nd in data.Nodes)
            {
                var node = nodeMap[nd.Id];

                // 设置父节点
                if (!string.IsNullOrEmpty(nd.ParentId) && nodeMap.TryGetValue(nd.ParentId, out var parent))
                    node.Parent = parent;

                // 设置子节点
                if (nd.ChildrenIds != null)
                {
                    foreach (var cid in nd.ChildrenIds)
                    {
                        if (nodeMap.TryGetValue(cid, out var child))
                            node.Children.Add(child);
                    }
                }
            }

            // 找到 Root（parentId 为 null 的节点）并设置
            var rootData = data.Nodes.FirstOrDefault(n => string.IsNullOrEmpty(n.ParentId));
            if (rootData != null && nodeMap.TryGetValue(rootData.Id, out var root))
                tree.Root = root;

            // 设置 ActiveLeaf
            if (!string.IsNullOrEmpty(data.ActiveLeafId) && nodeMap.TryGetValue(data.ActiveLeafId, out var leaf))
                tree.ActiveLeaf = leaf;
            else
                tree.ActiveLeaf = tree.Root;

            // 刷新所有有子节点的兄弟元数据
            foreach (var node in nodeMap.Values)
            {
                if (node.Children.Count > 0)
                    tree.RefreshSiblingMetadata(node);
            }

            return tree;
        }

        #endregion
    }
}
