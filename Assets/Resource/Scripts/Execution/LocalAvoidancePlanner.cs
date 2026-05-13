using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 局部避障规划器（承诺式绕行）：
/// 1. 扫描前方所有障碍物，将密集障碍合并为障碍簇。
/// 2. 选择左绕/右绕时，验证完整路径不与任何已知障碍冲突。
/// 3. 一旦决定绕行方向，持续保持承诺直到重接入主路径。
/// 4. 所有候选都不可行时，减速并请求上层重规划。
/// </summary>
public class LocalAvoidancePlanner
{
    public enum AvoidanceMode
    {
        FollowPath,
        BypassCommitted,
        BrakeAndReplanLocal,
    }

    public enum BypassSide
    {
        None = 0,
        Left = -1,
        Right = 1
    }

    public struct LocalObstacle
    {
        public string obstacleId;
        public Vector3 center;
        public float radius;
        public float expiryTime;
        public Collider sourceCollider;
    }

    public struct LocalPlanState
    {
        public AvoidanceMode mode;
        public BypassSide committedSide;
        public string obstacleId;
        public Vector3 obstacleCenter;
        public float obstacleRadius;
        public Vector3 bypassPoint;
        public Vector3 rejoinPoint;
        public bool headingToRejoin;
        public float lastPlanTime;
        public int failedPlanCount;

        public bool HasCommittedBypass =>
            committedSide != BypassSide.None &&
            !string.IsNullOrWhiteSpace(obstacleId);

        public void Clear()
        {
            mode = AvoidanceMode.FollowPath;
            committedSide = BypassSide.None;
            obstacleId = string.Empty;
            obstacleCenter = Vector3.zero;
            obstacleRadius = 0f;
            bypassPoint = Vector3.zero;
            rejoinPoint = Vector3.zero;
            headingToRejoin = false;
            lastPlanTime = 0f;
            failedPlanCount = 0;
        }
    }

    public struct LocalPlanContext
    {
        public Vector3 position;
        public Vector3 velocity;
        public Vector3 desiredTarget;
        public IReadOnlyList<Vector3> corridorPoints;
        public IReadOnlyList<LocalObstacle> obstacles;
        public float bodyRadius;
        public float safetyMargin;
        public float probeDistance;
        public float candidateForwardDistance;
        public float rejoinDistance;
        public float now;
        public Func<Vector3, Vector3, float, bool> isSegmentWalkable;
    }

    public struct LocalPlanResult
    {
        public bool valid;
        public Vector3 targetPoint;
        public float requestedSpeedScale;
        public AvoidanceMode mode;
        public BypassSide committedSide;
        public string obstacleId;
        public Vector3 rejoinPoint;
        public float nearestClearance;
        public bool shouldAdvanceWaypoints;
        public bool shouldReplan;
        public string debugReason;
    }

    private struct BypassCandidate
    {
        public bool valid;
        public BypassSide side;
        public Vector3 bypassPoint;
        public Vector3 rejoinPoint;
        public float score;
        public float speedScale;
        public string debugReason;
    }

    /// <summary>
    /// 合并后的障碍簇：多个密集障碍合并为一个大障碍。
    /// </summary>
    private struct ObstacleCluster
    {
        public string id;
        public Vector3 center;
        public float radius;
    }

    // 簇合并临时缓冲区（避免每帧 GC）
    private readonly List<ObstacleCluster> _clusterBuffer = new(16);
    private readonly List<LocalObstacle> _mergeBuffer = new(16);

    public LocalPlanResult Plan(ref LocalPlanState state, LocalPlanContext ctx)
    {
        LocalPlanResult result = CreateDefaultResult(ctx);
        Vector3 desiredDir = Flatten(ctx.desiredTarget - ctx.position).normalized;
        if (desiredDir.sqrMagnitude < 0.001f)
        {
            state.Clear();
            result.debugReason = "desired_target_too_close";
            return result;
        }

        Vector3 corridorAnchor = ResolveCorridorAnchor(ctx);

        // 构建障碍簇
        BuildObstacleClusters(ctx, desiredDir);

        bool hasBlocking = TryFindBlockingCluster(ctx, corridorAnchor, desiredDir,
            out ObstacleCluster blockingCluster, out float nearestClearance);
        result.nearestClearance = nearestClearance;

        // ── 已有承诺绕行 → 跟踪执行 ──
        if (state.HasCommittedBypass)
        {
            // 更新被跟踪障碍的最新位置
            UpdateTrackedObstacle(ref state, ctx.obstacles);

            if (ShouldSwitchToRejoin(state, ctx, corridorAnchor, desiredDir))
                state.headingToRejoin = true;

            result = BuildCommittedResult(state, ctx, corridorAnchor);
            result.nearestClearance = nearestClearance;

            // 检查承诺路径是否与其他障碍冲突
            if (IsCommittedPathBlocked(state, ctx))
            {
                state.Clear();
                // 立刻重新规划，不等下一帧
                hasBlocking = TryFindBlockingCluster(ctx, corridorAnchor, desiredDir,
                    out blockingCluster, out nearestClearance);
                result.nearestClearance = nearestClearance;
                // fall through to new bypass planning below
            }
            else if (ShouldFinishCommittedBypass(state, ctx, corridorAnchor))
            {
                state.Clear();
                result = CreateDefaultResult(ctx);
                result.shouldAdvanceWaypoints = true;
                result.debugReason = "rejoin_complete";
                return result;
            }
            else
            {
                return result;
            }
        }

        // ── 前方无障碍 → 正常跟随路径 ──
        if (!hasBlocking)
        {
            state.Clear();

            // 提前减速：前方有近距离障碍（但还没到阻挡阈值）
            if (nearestClearance < float.MaxValue && nearestClearance < ctx.safetyMargin * 3f)
            {
                result.requestedSpeedScale = Mathf.Lerp(0.5f, 1f,
                    Mathf.Clamp01(nearestClearance / (ctx.safetyMargin * 3f)));
                result.debugReason = "preemptive_slowdown";
            }
            else
            {
                result.debugReason = "corridor_clear";
            }
            return result;
        }

        // ── 有阻挡障碍 → 尝试规划绕行 ──
        if (TrySelectBestCandidate(ctx, corridorAnchor, desiredDir, blockingCluster,
            out BypassCandidate bestCandidate))
        {
            state.mode = AvoidanceMode.BypassCommitted;
            state.committedSide = bestCandidate.side;
            state.obstacleId = blockingCluster.id;
            state.obstacleCenter = blockingCluster.center;
            state.obstacleRadius = blockingCluster.radius;
            state.bypassPoint = bestCandidate.bypassPoint;
            state.rejoinPoint = bestCandidate.rejoinPoint;
            state.headingToRejoin = false;
            state.lastPlanTime = ctx.now;
            state.failedPlanCount = 0;

            result.valid = true;
            result.targetPoint = bestCandidate.bypassPoint;
            result.requestedSpeedScale = bestCandidate.speedScale;
            result.mode = state.mode;
            result.committedSide = state.committedSide;
            result.obstacleId = state.obstacleId;
            result.rejoinPoint = state.rejoinPoint;
            result.nearestClearance = nearestClearance;
            result.debugReason = bestCandidate.debugReason;
            return result;
        }

        // ── 所有候选失败 → 减速，请求上层重规划 ──
        state.failedPlanCount++;
        state.mode = AvoidanceMode.BrakeAndReplanLocal;

        result.valid = true;
        result.targetPoint = ctx.position; // 原地悬停
        result.requestedSpeedScale = 0.15f;
        result.mode = state.mode;
        result.committedSide = BypassSide.None;
        result.obstacleId = blockingCluster.id;
        result.rejoinPoint = corridorAnchor;
        result.shouldReplan = state.failedPlanCount >= 2;
        result.debugReason = "no_candidate_brake";
        return result;
    }

    // ==================================================================
    // 障碍簇合并
    // ==================================================================

    /// <summary>
    /// 将密集障碍合并为簇。两个障碍之间间距 &lt; bodyRadius*2 时合并，
    /// 合并后的簇中心取加权平均，半径取包围所有成员的最小圆。
    /// </summary>
    private void BuildObstacleClusters(LocalPlanContext ctx, Vector3 desiredDir)
    {
        _clusterBuffer.Clear();
        if (ctx.obstacles == null || ctx.obstacles.Count == 0) return;

        // 筛选前方相关障碍
        _mergeBuffer.Clear();
        float mergeThreshold = ctx.bodyRadius * 2.5f + ctx.safetyMargin;

        for (int i = 0; i < ctx.obstacles.Count; i++)
        {
            LocalObstacle obs = ctx.obstacles[i];
            if (string.IsNullOrWhiteSpace(obs.obstacleId)) continue;

            Vector3 toObs = Flatten(obs.center - ctx.position);
            float dist = toObs.magnitude;
            if (dist > ctx.probeDistance + obs.radius + ctx.safetyMargin) continue;

            _mergeBuffer.Add(obs);
        }

        if (_mergeBuffer.Count == 0) return;

        // Union-Find 风格的贪婪合并
        bool[] merged = new bool[_mergeBuffer.Count];

        for (int i = 0; i < _mergeBuffer.Count; i++)
        {
            if (merged[i]) continue;

            Vector3 clusterCenter = _mergeBuffer[i].center;
            float clusterRadius = _mergeBuffer[i].radius;
            string clusterId = _mergeBuffer[i].obstacleId;
            merged[i] = true;

            // 迭代合并：每次找到新成员后重新扫描
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int j = 0; j < _mergeBuffer.Count; j++)
                {
                    if (merged[j]) continue;

                    float gap = Flatten(clusterCenter - _mergeBuffer[j].center).magnitude
                                - clusterRadius - _mergeBuffer[j].radius;

                    if (gap <= mergeThreshold)
                    {
                        // 合并：扩展包围圆
                        Vector3 newCenter;
                        float newRadius;
                        MergeCircles(clusterCenter, clusterRadius,
                            _mergeBuffer[j].center, _mergeBuffer[j].radius,
                            out newCenter, out newRadius);
                        clusterCenter = newCenter;
                        clusterRadius = newRadius;
                        clusterId += "+" + _mergeBuffer[j].obstacleId;
                        merged[j] = true;
                        changed = true;
                    }
                }
            }

            _clusterBuffer.Add(new ObstacleCluster
            {
                id = clusterId,
                center = clusterCenter,
                radius = clusterRadius,
            });
        }
    }

    /// <summary>
    /// 合并两个圆为最小包围圆。
    /// </summary>
    private static void MergeCircles(Vector3 c1, float r1, Vector3 c2, float r2,
        out Vector3 center, out float radius)
    {
        Vector3 c1f = Flatten(c1);
        Vector3 c2f = Flatten(c2);
        float dist = (c2f - c1f).magnitude;

        if (dist + r2 <= r1)
        {
            // c2 完全在 c1 内
            center = c1; radius = r1; return;
        }
        if (dist + r1 <= r2)
        {
            // c1 完全在 c2 内
            center = c2; radius = r2; return;
        }

        radius = (dist + r1 + r2) * 0.5f;
        float t = (radius - r1) / Mathf.Max(0.001f, dist);
        center = Vector3.Lerp(c1, c2, t);
        center.y = c1.y;
    }

    // ==================================================================
    // 障碍检测
    // ==================================================================

    private bool TryFindBlockingCluster(
        LocalPlanContext ctx,
        Vector3 corridorAnchor,
        Vector3 desiredDir,
        out ObstacleCluster blockingCluster,
        out float nearestClearance)
    {
        blockingCluster = default;
        nearestClearance = float.MaxValue;
        if (_clusterBuffer.Count == 0) return false;

        Vector3 pos = ctx.position;
        Vector3 anchor = corridorAnchor;
        anchor.y = pos.y;
        float segmentLength = Flatten(anchor - pos).magnitude;
        if (segmentLength < 0.5f)
            segmentLength = Mathf.Max(1f, ctx.probeDistance * 0.6f);

        float bestProjection = float.MaxValue;
        for (int i = 0; i < _clusterBuffer.Count; i++)
        {
            ObstacleCluster cluster = _clusterBuffer[i];

            Vector3 toCluster = Flatten(cluster.center - pos);
            float forwardProjection = Vector3.Dot(toCluster, desiredDir);
            if (forwardProjection < -ctx.bodyRadius) continue;
            if (forwardProjection > ctx.probeDistance + cluster.radius + ctx.safetyMargin) continue;

            float pathDistance = DistancePointToSegmentXZ(cluster.center, pos, anchor);
            float clearance = pathDistance - (cluster.radius + ctx.bodyRadius + ctx.safetyMargin);
            nearestClearance = Mathf.Min(nearestClearance, clearance);

            if (clearance > Mathf.Max(0.1f, ctx.safetyMargin * 0.35f)) continue;

            if (forwardProjection < bestProjection)
            {
                bestProjection = forwardProjection;
                blockingCluster = cluster;
            }
        }

        return !string.IsNullOrWhiteSpace(blockingCluster.id);
    }

    // ==================================================================
    // 绕行候选评估
    // ==================================================================

    private bool TrySelectBestCandidate(
        LocalPlanContext ctx,
        Vector3 corridorAnchor,
        Vector3 desiredDir,
        ObstacleCluster blockingCluster,
        out BypassCandidate bestCandidate)
    {
        bestCandidate = default;
        float bestScore = float.MinValue;
        BypassSide[] sides = { BypassSide.Left, BypassSide.Right };
        float[] lateralScales = { 1.0f, 1.35f, 1.7f, 2.2f };
        float[] forwardScales = { 1.0f, 1.35f, 1.7f };

        for (int s = 0; s < sides.Length; s++)
        {
            for (int l = 0; l < lateralScales.Length; l++)
            {
                for (int f = 0; f < forwardScales.Length; f++)
                {
                    BypassCandidate candidate = BuildCandidate(
                        ctx, corridorAnchor, desiredDir,
                        blockingCluster, sides[s],
                        lateralScales[l], forwardScales[f]);

                    if (!candidate.valid) continue;
                    if (candidate.score <= bestScore) continue;

                    bestScore = candidate.score;
                    bestCandidate = candidate;
                }
            }
        }

        return bestCandidate.valid;
    }

    private BypassCandidate BuildCandidate(
        LocalPlanContext ctx,
        Vector3 corridorAnchor,
        Vector3 desiredDir,
        ObstacleCluster blockingCluster,
        BypassSide side,
        float lateralScale,
        float forwardScale)
    {
        BypassCandidate candidate = default;
        int sideSign = side == BypassSide.Right ? 1 : -1;
        Vector3 right = Vector3.Cross(Vector3.up, desiredDir).normalized;
        if (right.sqrMagnitude < 0.001f) return candidate;

        float requiredRadius = blockingCluster.radius + ctx.bodyRadius + ctx.safetyMargin * lateralScale;
        float forwardLead = Mathf.Max(ctx.candidateForwardDistance * forwardScale, requiredRadius * 1.15f);
        float obstacleProjection = Mathf.Max(0f, Vector3.Dot(
            Flatten(blockingCluster.center - ctx.position), desiredDir));

        Vector3 anchorOnPath = ctx.position + desiredDir * obstacleProjection;
        Vector3 bypassPoint = anchorOnPath + right * sideSign * requiredRadius;
        Vector3 rejoinPoint = blockingCluster.center + desiredDir * forwardLead
                              + right * sideSign * (ctx.bodyRadius + ctx.safetyMargin * 0.25f);
        bypassPoint.y = ctx.desiredTarget.y;
        rejoinPoint.y = ctx.desiredTarget.y;

        // 验证 bypass 路径不与 blocking cluster 碰撞
        if (DistancePointToSegmentXZ(blockingCluster.center, ctx.position, bypassPoint)
            <= blockingCluster.radius + ctx.bodyRadius)
            return candidate;
        if (DistancePointToSegmentXZ(blockingCluster.center, bypassPoint, rejoinPoint)
            <= blockingCluster.radius + ctx.bodyRadius)
            return candidate;

        // 验证 bypass 路径不与任何其他障碍簇碰撞
        for (int i = 0; i < _clusterBuffer.Count; i++)
        {
            ObstacleCluster other = _clusterBuffer[i];
            if (other.id == blockingCluster.id) continue;

            float minClear = other.radius + ctx.bodyRadius;
            if (DistancePointToSegmentXZ(other.center, ctx.position, bypassPoint) <= minClear)
                return candidate;
            if (DistancePointToSegmentXZ(other.center, bypassPoint, rejoinPoint) <= minClear)
                return candidate;
        }

        // 验证网格可行走
        if (ctx.isSegmentWalkable != null)
        {
            if (!ctx.isSegmentWalkable(ctx.position, bypassPoint, ctx.bodyRadius))
                return candidate;
            if (!ctx.isSegmentWalkable(bypassPoint, rejoinPoint, ctx.bodyRadius))
                return candidate;
            if (!ctx.isSegmentWalkable(rejoinPoint, corridorAnchor, ctx.bodyRadius))
                return candidate;
        }

        // 评分
        Vector3 bypassDir = Flatten(bypassPoint - ctx.position).normalized;
        float turnPenalty = Vector3.Angle(desiredDir, bypassDir);
        float progressScore = Vector3.Dot(Flatten(rejoinPoint - ctx.position), desiredDir);
        float rejoinPenalty = Flatten(corridorAnchor - rejoinPoint).magnitude;
        float smoothPenalty = Flatten(rejoinPoint - bypassPoint).magnitude * 0.08f;
        float clearanceBonus = requiredRadius;

        candidate.valid = true;
        candidate.side = side;
        candidate.bypassPoint = bypassPoint;
        candidate.rejoinPoint = rejoinPoint;
        candidate.speedScale = Mathf.Clamp01(Mathf.Lerp(0.35f, 0.82f,
            Mathf.Clamp01(progressScore / Mathf.Max(1f, ctx.probeDistance))));
        candidate.score = progressScore * 1.25f + clearanceBonus * 0.9f
                          - rejoinPenalty * 0.45f - turnPenalty * 0.05f - smoothPenalty;
        candidate.debugReason = $"commit_{side.ToString().ToLowerInvariant()}";
        return candidate;
    }

    // ==================================================================
    // 承诺跟踪
    // ==================================================================

    private static void UpdateTrackedObstacle(ref LocalPlanState state, IReadOnlyList<LocalObstacle> obstacles)
    {
        if (obstacles == null) return;
        // obstacleId 可能是合并的 "id1+id2+id3"，匹配任何一个子 ID 即更新
        for (int i = 0; i < obstacles.Count; i++)
        {
            if (state.obstacleId.Contains(obstacles[i].obstacleId))
            {
                // 用第一个匹配的更新位置（簇中心近似）
                state.obstacleCenter = obstacles[i].center;
                state.obstacleRadius = Mathf.Max(state.obstacleRadius, obstacles[i].radius);
                return;
            }
        }
    }

    /// <summary>
    /// 检查当前承诺的绕行路径是否被其他（新出现的）障碍阻挡。
    /// </summary>
    private bool IsCommittedPathBlocked(LocalPlanState state, LocalPlanContext ctx)
    {
        Vector3 currentTarget = state.headingToRejoin ? state.rejoinPoint : state.bypassPoint;
        float minClear = ctx.bodyRadius + ctx.safetyMargin * 0.5f;

        for (int i = 0; i < _clusterBuffer.Count; i++)
        {
            ObstacleCluster cluster = _clusterBuffer[i];
            if (state.obstacleId.Contains(cluster.id)) continue;

            if (DistancePointToSegmentXZ(cluster.center, ctx.position, currentTarget)
                <= cluster.radius + minClear)
                return true;
        }
        return false;
    }

    private static LocalPlanResult BuildCommittedResult(LocalPlanState state, LocalPlanContext ctx, Vector3 corridorAnchor)
    {
        Vector3 target = state.headingToRejoin ? state.rejoinPoint : state.bypassPoint;
        float speedScale = state.headingToRejoin ? 0.72f : 0.48f;
        return new LocalPlanResult
        {
            valid = true,
            targetPoint = target,
            requestedSpeedScale = speedScale,
            mode = state.mode,
            committedSide = state.committedSide,
            obstacleId = state.obstacleId,
            rejoinPoint = state.rejoinPoint,
            nearestClearance = float.MaxValue,
            shouldAdvanceWaypoints = false,
            debugReason = state.headingToRejoin ? "tracking_rejoin" : "tracking_bypass",
        };
    }

    private static bool ShouldSwitchToRejoin(LocalPlanState state, LocalPlanContext ctx, Vector3 corridorAnchor, Vector3 desiredDir)
    {
        if (state.headingToRejoin) return false;

        float distToBypass = Flatten(state.bypassPoint - ctx.position).magnitude;
        if (distToBypass <= ctx.rejoinDistance)
            return true;

        float passedProjection = Vector3.Dot(Flatten(ctx.position - state.obstacleCenter), desiredDir);
        return passedProjection > state.obstacleRadius + ctx.bodyRadius + ctx.safetyMargin * 0.5f;
    }

    private static bool ShouldFinishCommittedBypass(LocalPlanState state, LocalPlanContext ctx, Vector3 corridorAnchor)
    {
        if (!state.headingToRejoin) return false;

        float distToRejoin = Flatten(state.rejoinPoint - ctx.position).magnitude;
        if (distToRejoin <= ctx.rejoinDistance)
            return true;

        float distToCorridor = DistancePointToSegmentXZ(ctx.position, state.rejoinPoint, corridorAnchor);
        return distToCorridor <= ctx.rejoinDistance * 0.8f;
    }

    // ==================================================================
    // 辅助
    // ==================================================================

    private static LocalPlanResult CreateDefaultResult(LocalPlanContext ctx)
    {
        return new LocalPlanResult
        {
            valid = true,
            targetPoint = ctx.desiredTarget,
            requestedSpeedScale = 1f,
            mode = AvoidanceMode.FollowPath,
            committedSide = BypassSide.None,
            obstacleId = string.Empty,
            rejoinPoint = ctx.desiredTarget,
            nearestClearance = float.MaxValue,
            shouldAdvanceWaypoints = false,
            shouldReplan = false,
            debugReason = string.Empty,
        };
    }

    private static Vector3 ResolveCorridorAnchor(LocalPlanContext ctx)
    {
        if (ctx.corridorPoints == null || ctx.corridorPoints.Count == 0)
            return ctx.desiredTarget;

        Vector3 pos = ctx.position;
        Vector3 best = ctx.desiredTarget;
        float bestDist = 0f;
        for (int i = 0; i < ctx.corridorPoints.Count; i++)
        {
            Vector3 candidate = ctx.corridorPoints[i];
            float dist = Flatten(candidate - pos).magnitude;
            if (dist <= bestDist) continue;
            if (dist > ctx.probeDistance * 1.2f) continue;
            best = candidate;
            bestDist = dist;
        }
        return best;
    }

    private static float DistancePointToSegmentXZ(Vector3 point, Vector3 a, Vector3 b)
    {
        Vector3 point2 = Flatten(point);
        Vector3 a2 = Flatten(a);
        Vector3 b2 = Flatten(b);
        Vector3 ab = b2 - a2;
        float abLenSq = ab.sqrMagnitude;
        if (abLenSq < 0.0001f) return (point2 - a2).magnitude;

        float t = Mathf.Clamp01(Vector3.Dot(point2 - a2, ab) / abLenSq);
        Vector3 closest = a2 + ab * t;
        return (point2 - closest).magnitude;
    }

    private static Vector3 Flatten(Vector3 value)
    {
        value.y = 0f;
        return value;
    }
}
