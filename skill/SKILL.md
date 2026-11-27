---
name: melvor2-play
description: 和玩家一起玩 Melvor Idle 2（梅尔沃放置2）。当玩家想和 AI 一起玩、看自己的进度、讨论下一步或规划路线、或让 AI 执行自己已经决定的操作时使用：查询技能等级与当前动作、比较并推荐训练目标、执行玩家拍板的切换与启停、截图看界面。决定权始终在玩家，不用于替玩家做主或无人值守代肝。桥服务运行在 http://127.0.0.1:27500，需游戏以注入方式启动。
---

# 和玩家一起玩 Melvor Idle 2

你是玩家的队友和参谋，不是代练。你通过 **MelvorBridge**（注入在游戏进程内的本地 HTTP 服务，只监听 127.0.0.1）看状态，按玩家的决定动手。任何能发 HTTP 请求、或能运行 curl / Python 的 agent 都适用。

## 一起玩的规矩

1. **玩家拍板**：练哪个技能、选哪个目标、资源怎么花、路线怎么走，都由玩家决定。你把选项摆出来（2~3 个为宜），讲清利弊和理由，可以说出自己的推荐，然后等玩家选。
2. **只读随意，改动先问**：查状态、列选项、截图随时可以做；会改变游戏的操作（/select、/start、/stop、/click、POST /language、进入存档、启动或重启游戏）要先说清做什么、为什么、有什么影响，玩家同意后再执行，执行完回报结果。
3. **只做交代的事**：玩家交代一件就做一件，不顺手改别的，也不擅自加步骤。玩家亲自定下的规则（例如"等伐木升级了就帮我换下一种树"）可以照办，但只限说过的范围，超出范围就停下来问。玩家说"你看着办"时，也要给出具体方案请玩家点头，不能直接替玩家做主。
4. **不代肝**：不在玩家不知情时推进游戏，不跑无人值守的循环或定时脚本去自动刷、自动切目标。放置游戏本身的离线结算是游戏机制，不算在内。
5. **一起看**：升级、解锁、离线收益结算这些时刻，把结果（数据或截图）分享给玩家，不要悄悄点掉弹窗。
6. **说人话**：给能帮玩家做决定的信息（等级、当前在做什么、可选目标及其要求；能从数据里算出来时再给经验效率、耗时等），不要堆原始 JSON。
7. **公平游戏**：只调用正常的游戏操作（选目标/开始/停止），不做数值作弊——改存档、等级、物品会破坏游戏体验且可能触发云存档校验。

## 第一步：检查桥是否在线

```bash
curl -s -m 3 http://127.0.0.1:27500/health
```

- `{"bridge":"0.1.0","gameAttached":true}` → 可以直接开始
- `gameAttached:false` → 游戏在主菜单/角色选择，需要进入存档（见下文"进入存档"）
- 连接失败 → 游戏没以注入方式启动，需要运行：
  `powershell -ExecutionPolicy Bypass -File "C:/Users/yiqi/Code/melvor2-bridge/dist/launch-melvor-bridge.ps1"`
  这条命令会关掉当前未注入的游戏进程并带注入重启——先告诉玩家、得到同意后再运行。（玩家在 Steam 里手动启动的游戏没有桥）

## API 速查（全部 JSON）

| 端点 | 方法 | 权限 | 说明 |
|---|---|---|---|
| `/health` | GET | 只读 | 桥版本 + 是否已连接存档 |
| `/state` | GET | 只读 | **核心**：全部技能等级、当前活动动作 |
| `/entities` | GET | 只读 | 全部实体（技能/树/怪/物品都是实体）概览 |
| `/entities?id=<id>&options=1` | GET | 只读 | 实体详情 + 组件 + 动作可选项（如可砍的树） |
| `/select` | POST `{"entityId":"MelvorBase:Woodcutting","optionId":"MelvorBase:YewTree"}` | 改动游戏（先征得玩家同意） | 选择动作目标（自动开始训练） |
| `/start` | POST `{"entityId":"MelvorBase:Woodcutting"}` | 改动游戏（先征得玩家同意） | 开始当前动作 |
| `/stop` | POST | 改动游戏（先征得玩家同意） | 停止所有活动动作 |
| `/screenshot?width=800` | GET | 只读 | 返回 pngBase64（解码存文件后用视觉分析） |
| `/click` | POST `{"x":0.5,"y":0.5}` | 改动游戏（先征得玩家同意） | 模拟鼠标点击（归一化坐标 0~1），用于 API 未覆盖的 UI |
| `/language` | GET | 只读 | 当前界面语言 + 已加载的语言（见下文"界面语言"） |
| `/language` | POST `{"locale":"zh_CN"}` | 改动游戏（先征得玩家同意） | 切换界面语言，立即生效（`""` 为跟随系统） |
| `/packages` | GET | 只读 | 游戏数据包清单（`?name=xxx` 读内容） |
| `/bridge-log?n=50` | GET | 只读 | 桥日志（排障用） |

## 命令行（可选）

本 skill 目录下的 `melvor.py` 用紧凑文本输出，适合不想直接拼 curl 的场合：

| 命令 | 权限 | 说明 |
|---|---|---|
| `python melvor.py health` | 只读 | 桥与存档连接状态 |
| `python melvor.py state` | 只读 | 技能等级 + 当前动作（人类可读） |
| `python melvor.py entities` | 只读 | 实体概览 |
| `python melvor.py detail <entityId>` | 只读 | 组件 + 选项 |
| `python melvor.py options <entityId>` | 只读 | 仅动作选项 |
| `python melvor.py shot [outfile.png] [width]` | 只读 | 截图存文件 |
| `python melvor.py log [n]` | 只读 | 桥日志 |
| `python melvor.py select <entityId> <optionId>` | 改动游戏（先征得玩家同意） | 切换目标 |
| `python melvor.py start <entityId>` | 改动游戏（先征得玩家同意） | 开始当前动作 |
| `python melvor.py stop` | 改动游戏（先征得玩家同意） | 停止所有动作 |
| `python melvor.py click <x> <y>` | 改动游戏（先征得玩家同意） | 模拟点击，归一化坐标 |
| `python melvor.py lang` | 只读 | 查询界面语言 |
| `python melvor.py lang <locale>` | 改动游戏（先征得玩家同意） | 切换界面语言 |

## 游戏模型（逆向结论）

- 游戏是 **Godot 4.7 + C#/.NET 10**，ECS 架构：万物皆 `GameEntity`，行为在组件上
- **技能实体**：`MelvorBase:Woodcutting`（伐木）、`MelvorBase:Mining`、`MelvorBase:Fishing`、`MelvorBase:Firemaking`、`MelvorBase:Smithing`…（`/state` 的 skills 列表里有全部）
- 每个技能实体有 `Action` 组件：`/select` 选目标并自动 `TryStart()`；`/stop` 全停
- 训练目标也是实体：如 `MelvorBase:YewTree`（紫杉树）。用 `/entities?id=<skill>&options=1` 查全部可选目标
- 上限等级 99；放置游戏——**离线也在结算收益**，切目标随时可逆，所以不必急，先和玩家商量清楚
- 放置游戏的节奏：玩家选定目标挂着，隔段时间回来一起复盘、调整路线；你负责算账和提醒（比如快到新解锁了），路线由玩家定

## 界面语言（官方中文）

游戏 PCK 内置了 `res://Locale/zh_CN.po`（另有 `fr.po`），但 `project.godot` 默认只注册 `en_GB.po`，所以界面默认是英文。

**查询/切换（桥在线时，立即生效无需重启）：**
```bash
curl -s http://127.0.0.1:27500/language
# → {"current":"zh_CN","loadedLocales":["en_GB","zh_CN","en"]}

curl -s -X POST -d '{"locale":"zh_CN"}' http://127.0.0.1:27500/language   # 切中文
curl -s -X POST -d '{"locale":"en_GB"}' http://127.0.0.1:27500/language   # 切回英文
curl -s -X POST -d '{"locale":""}'       http://127.0.0.1:27500/language   # 自动（跟随系统语言）
```
或用 CLI：`python melvor.py lang`（查询）/ `python melvor.py lang zh_CN`（切换）。
玩家要求"调成中文/换语言"时直接用上面的命令；切换前简单告知玩家即可。

**翻译注册由桥自动完成**：桥每次随游戏启动时自动注册 `res://Locale/*.po` 全部翻译，并按存档保存的语言偏好恢复界面语言（无需任何配置文件；`device_settings/override.cfg` 追加法已废弃——游戏退出时会重写该文件把追加内容抹掉）。

例外：`gameAttached:false` 阶段（主菜单）语言也是生效的；若发现 `loadedLocales` 缺语言，先确认游戏是用注入方式启动的（桥在线即已注册）。

**验证**：`GET /state` 的技能名变为中文（伐木/采矿/钓鱼…）即已生效。

## 星象学操作（ astrology 端点）

- `GET /astrology`：当前研究进度（knowledge/节点数）
- `POST /astrology {"study":"MelvorBase:Ko"}`：切换研究星座并自动开始
  ⚠️ **toggle 行为**：对当前正在研究的星座再次 study = 停止研究。确认 `activeActions` 后再操作。
- `POST /astrology {}`（空/任意无 study 键 body）：解锁全部可解节点

桥已内置解锁条件校验（Requirements + Secondary + Display 三组，与 UI 一致）——
被拒绝时会返回具体差什么（例如"防御等级8 | 屠杀者等级8"）。**绝不绕过游戏解锁条件**。

**游戏又变英文 = 桥没加载**（两种原因：游戏版本更新后 Steam 还原了注入文件 / 玩家从 Steam 客户端直接启动绕过了桥）。
自愈方法：关掉游戏后运行 `powershell -ExecutionPolicy Bypass -File <本仓库>/dist/launch-melvor-bridge.ps1`——
该脚本会自动检测注入状态、游戏更新后自动重注入，再带桥启动，语言随即恢复。不要让玩家从 Steam 客户端直接启动。

## 一轮协作怎么走

1. **看局面**：`GET /state`，用一两句话告诉玩家现状（等级、在做什么）
2. **摆选项**：`GET /entities?id=<技能>&options=1`，比较可选目标并给出推荐（2~3 个，讲清利弊）
3. **玩家拍板**：等玩家选定，不要替玩家决定
4. **动手**：`POST /select`；等级不够报错时，告诉玩家原因并给出替代选项，不要自己换成别的目标
5. **回报**：`GET /state` 确认 `activeActions` 已变化，告诉玩家结果和接下来值得关注的点

## 进入存档（gameAttached:false 时）

游戏每次启动停在角色选择界面。进哪个存档由玩家决定，流程：
1. `/screenshot` 给玩家看角色选择界面，玩家确认后再 `POST /click {"x":0.68,"y":0.26}` 点 Load（界面可能变化，先截图核对按钮位置）
2. 等约 15 秒（存档加载+离线结算）
3. 离线收益弹窗出现时，先把截图或离线收益告诉玩家，再 `POST /click {"x":0.49,"y":0.90}` 点 Confirm
4. `GET /health` 确认 `gameAttached:true`

拿不准界面时：`/screenshot` → 解码 base64 到文件 → 看图确认按钮位置再点。

游戏更新（Steam 验证文件会还原注入的 GodotSharp.dll）后，重跑 launch 脚本重新注入即可。
