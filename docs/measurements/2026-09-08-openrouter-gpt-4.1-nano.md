### Run: `openai/gpt-4.1-nano` via openai (https://openrouter.ai/api/v1), thinking off, 10 runs/task, 2026-09-08 20:25 UTC
Model info: `{"id":"openai/gpt-4.1-nano","canonical_slug":"openai/gpt-4.1-nano-2025-04-14","hugging_face_id":"","name":"OpenAI: GPT-4.1 Nano","created":1744651369,"description":"For tasks that demand low latency, GPT‑4.1 nano is the fastest and cheapest model in the GPT-4.1 series. It delivers exceptional performance at a small size with its 1 million...","context_length":1047576,"architecture":{"modality":"text+image+file->text","input_modalities":["image","text","file"],"output_modalities":["text"],"tokenizer":"GPT","instruct_type":null},"pricing":{"prompt":"0.0000001","completion":"0.0000004","web_search":"0.01","input_cache_read":"0.000000025"},"top_provider":{"context_length":1047576,"max_completion_tokens":32768,"is_moderated":true},"per_request_limits":null,"supported_parameters":["max_completion_tokens","max_tokens","response_format","seed","structured_outputs","temperature","tool_choice","tools","top_p"],"default_parameters":{},"supported_voices":null,"knowledge_cutoff":"2024-06-30","expiration_date":null,"links":{"details":"/api/v1/models/openai/gpt-4.1-nano-2025-04-14/endpoints"},"benchmarks":{"design_arena":[{"arena":"models","category":"3d","elo":957,"win_rate":46,"rank":106},{"arena":"models","category":"codecategories","elo":980,"win_rate":47.3,"rank":113},{"arena":"models","category":"dataviz","elo":907,"win_rate":41.1,"rank":116},{"arena":"models","category":"gamedev","elo":996,"win_rate":49.6,"rank":109},{"arena":"models","category":"uicomponent","elo":934,"win_rate":43.9,"rank":111},{"arena":"models","category":"website","elo":987,"win_rate":48.1,"rank":115}],"artificial_analysis":{"intelligence_index":null,"coding_index":11.1,"agentic_index":null}}}`

#### Reliability by condition and task

| Condition | Task | Runs | First-call success | Completed | Wrong tool first | Avg steps to done | Avg turns | Avg input tok | Avg output tok | Errors |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Eager | calendar_tomorrow | 10 | 10/10 (100%) | 10/10 (100%) | 0 | 1.0 | 1.0 | 2245 | 53 | 0 |
| Eager | docs_search | 10 | 9/10 (90%) | 9/10 (90%) | 0 | 1.3 | 1.4 | 3476 | 44 | 0 |
| Eager | list_files_marimer | 10 | 10/10 (100%) | 10/10 (100%) | 0 | 1.0 | 1.0 | 2256 | 32 | 0 |
| Eager | list_files_personal | 10 | 10/10 (100%) | 10/10 (100%) | 0 | 1.0 | 1.0 | 2252 | 26 | 0 |
| Eager | send_email | 10 | 6/10 (60%) | 10/10 (100%) | 4 | 1.4 | 1.4 | 3193 | 56 | 0 |
| InvokeTool | calendar_tomorrow | 10 | 0/10 (0%) | 4/10 (40%) | 7 | 6.5 | 6.7 | 18083 | 225 | 0 |
| InvokeTool | docs_search | 10 | 2/10 (20%) | 2/10 (20%) | 0 | 4.5 | 4.8 | 10964 | 163 | 0 |
| InvokeTool | list_files_marimer | 10 | 3/10 (30%) | 6/10 (60%) | 0 | 3.8 | 4.8 | 14900 | 113 | 0 |
| InvokeTool | list_files_personal | 10 | 0/10 (0%) | 0/10 (0%) | 0 | - | 8.0 | 35793 | 101 | 0 |
| InvokeTool | send_email | 10 | 0/10 (0%) | 10/10 (100%) | 8 | 5.2 | 5.2 | 12669 | 200 | 0 |
| Lazy | calendar_tomorrow | 10 | 3/10 (30%) | 5/10 (50%) | 2 | 2.4 | 1.8 | 3533 | 68 | 0 |
| Lazy | docs_search | 10 | 6/10 (60%) | 6/10 (60%) | 0 | 2.0 | 2.2 | 4910 | 67 | 0 |
| Lazy | list_files_marimer | 10 | 10/10 (100%) | 10/10 (100%) | 0 | 2.0 | 2.0 | 4317 | 70 | 0 |
| Lazy | list_files_personal | 10 | 8/10 (80%) | 8/10 (80%) | 0 | 2.0 | 1.8 | 3895 | 63 | 0 |
| Lazy | send_email | 10 | 3/10 (30%) | 9/10 (90%) | 7 | 3.7 | 2.8 | 7191 | 1073 | 0 |

#### Reliability by condition (all tasks)

| Condition | Runs | First-call success | Completed | Avg input tok / run | Avg output tok / run | Avg ms / run |
|---|---:|---:|---:|---:|---:|---:|
| Eager | 50 | 45/50 (90%) | 49/50 (98%) | 2684 | 42 | 1966 |
| InvokeTool | 50 | 5/50 (10%) | 22/50 (44%) | 18482 | 160 | 6987 |
| Lazy | 50 | 30/50 (60%) | 38/50 (76%) | 4769 | 268 | 17050 |

#### tools/list size by condition

| Condition | Tools at start | Bytes at start | Tools at end | Bytes at end |
|---|---:|---:|---:|---:|
| Eager | 27 | 13583 | 27 | 13583 |
| Lazy | 14 | 7744 | 19 | 9824 |
| InvokeTool | 13 | 6994 | 13 | 6994 |

#### First-attempt failures (what the model actually sent)

| Condition | Task | Run | First downstream call | Result |
|---|---|---:|---|---|
| Eager | send_email | 3 | `adjutant__list_accounts({})` | ok, wrong tool |
| Eager | send_email | 5 | `adjutant__list_accounts({})` | ok, wrong tool |
| Eager | send_email | 8 | `adjutant__list_accounts({})` | ok, wrong tool |
| Eager | send_email | 10 | `adjutant__list_accounts({})` | ok, wrong tool |
| Eager | docs_search | 2 | (never reached a downstream) | I found some tools related to file listing and calendar events, but I didn't find specific documenta… |
| Lazy | send_email | 1 | `adjutant__list_accounts({})` | ok, wrong tool |
| Lazy | send_email | 3 | `adjutant__list_accounts({})` | ok, wrong tool |
| Lazy | send_email | 4 | `adjutant__list_accounts({})` | ok, wrong tool |
| Lazy | send_email | 5 | `adjutant__list_accounts({})` | ok, wrong tool |
| Lazy | send_email | 7 | `adjutant__list_accounts({})` | ok, wrong tool |
| Lazy | send_email | 8 | `adjutant__list_accounts({})` | ok, wrong tool |
| Lazy | send_email | 10 | `adjutant__list_accounts({})` | ok, wrong tool |
| Lazy | calendar_tomorrow | 3 | (never reached a downstream) | I couldn't retrieve your calendar information. Would you like me to help you set up access or check … |
| Lazy | calendar_tomorrow | 4 | `adjutant__list_accounts({})` | ok, wrong tool |
| Lazy | calendar_tomorrow | 5 | (never reached a downstream) | I don't have direct access to your personal calendar. If you can provide me with details or specify … |
| Lazy | calendar_tomorrow | 6 | (never reached a downstream) | I don't have access to your personal calendar. Would you like me to help you find or set up a calend… |
| Lazy | calendar_tomorrow | 7 | `adjutant__list_accounts({})` | ok, wrong tool |
| Lazy | calendar_tomorrow | 9 | (never reached a downstream) | I couldn't retrieve information about your calendar for tomorrow. Would you like me to help you find… |
| Lazy | calendar_tomorrow | 10 | (never reached a downstream) | I don't have direct access to your personal calendar. If you'd like, I can help you set up a reminde… |
| Lazy | list_files_personal | 3 | (never reached a downstream) | I don't have direct access to your personal OneDrive. However, I can guide you through how to view t… |
| Lazy | list_files_personal | 9 | (never reached a downstream) | I need to explore your OneDrive to find the Photos folder, but I don't have direct access to your pe… |
| Lazy | docs_search | 4 | (never reached a downstream) | I couldn't find specific documentation on configuring the options pattern in ASP.NET Core. Would you… |
| Lazy | docs_search | 6 | (never reached a downstream) | I found some related tools but not the specific documentation. Would you like me to list the options… |
| Lazy | docs_search | 9 | (never reached a downstream) | I couldn't find specific documentation from Microsoft on configuring the options pattern in ASP.NET … |
| Lazy | docs_search | 10 | (never reached a downstream) | I found some related tools for accessing documentation and files, but I couldn't retrieve the specif… |
| InvokeTool | send_email | 1 | `invoke_tool({"serverName":"adjutant","toolName":"adjutant__send_email","arguments":"{\u0022to\u0022:[\u0022alice@example.com\u0022],\u0022subject\u0022:…)` | error: Unknown tool 'adjutant__send_email' on server 'adjutant'. Available tools: [create_calendar_event, get_calendar_events, list_accounts, send_… |
| InvokeTool | send_email | 2 | `invoke_tool({"serverName":"adjutant","toolName":"send_email","arguments":"{\u0022to\u0022:\u0022alice@example.com\u0022,\u0022subject\u0022:\u0022Lunch …)` | error: An error occurred invoking 'send_email'. Argument mismatch for tool 'send_email' on 'adjutant'. Every key matched the schema, so a supplied … |
| InvokeTool | send_email | 3 | `invoke_tool({"serverName":"adjutant","toolName":"adjutant__send_email","arguments":"{\u0022to\u0022:\u0022alice@example.com\u0022,\u0022subject\u0022:\u…)` | error: Unknown tool 'adjutant__send_email' on server 'adjutant'. Available tools: [create_calendar_event, get_calendar_events, list_accounts, send_… |
| InvokeTool | send_email | 4 | `invoke_tool({"serverName":"adjutant","toolName":"adjutant__send_email","arguments":"{\u0022to\u0022:\u0022alice@example.com\u0022,\u0022subject\u0022:\u…)` | error: Unknown tool 'adjutant__send_email' on server 'adjutant'. Available tools: [create_calendar_event, get_calendar_events, list_accounts, send_… |
| InvokeTool | send_email | 5 | `invoke_tool({"serverName":"adjutant","toolName":"adjutant__send_email","arguments":"{\u0022to\u0022:\u0022alice@example.com\u0022,\u0022subject\u0022:\u…)` | error: Unknown tool 'adjutant__send_email' on server 'adjutant'. Available tools: [create_calendar_event, get_calendar_events, list_accounts, send_… |
| InvokeTool | send_email | 6 | `invoke_tool({"serverName":"adjutant","toolName":"adjutant__send_email","arguments":"{\u0022to\u0022:\u0022alice@example.com\u0022,\u0022subject\u0022:\u…)` | error: Unknown tool 'adjutant__send_email' on server 'adjutant'. Available tools: [create_calendar_event, get_calendar_events, list_accounts, send_… |
| InvokeTool | send_email | 7 | `invoke_tool({"serverName":"adjutant","toolName":"adjutant__send_email","arguments":"{\u0022to\u0022: \u0022alice@example.com\u0022, \u0022subject\u0022:…)` | error: Unknown tool 'adjutant__send_email' on server 'adjutant'. Available tools: [create_calendar_event, get_calendar_events, list_accounts, send_… |
| InvokeTool | send_email | 8 | `invoke_tool({"serverName":"adjutant","toolName":"send_email","arguments":"{\u0022to\u0022: \u0022alice@example.com\u0022, \u0022subject\u0022: \u0022Lun…)` | error: An error occurred invoking 'send_email'. Argument mismatch for tool 'send_email' on 'adjutant'. Every key matched the schema, so a supplied … |
| InvokeTool | send_email | 9 | `invoke_tool({"serverName":"adjutant","toolName":"adjutant__send_email","arguments":"{\u0022to\u0022:\u0022alice@example.com\u0022,\u0022subject\u0022:\u…)` | error: Unknown tool 'adjutant__send_email' on server 'adjutant'. Available tools: [create_calendar_event, get_calendar_events, list_accounts, send_… |
| InvokeTool | send_email | 10 | `invoke_tool({"serverName":"adjutant","toolName":"adjutant__send_email","arguments":"{\u0022recipient\u0022:\u0022alice@example.com\u0022,\u0022subject\u…)` | error: Unknown tool 'adjutant__send_email' on server 'adjutant'. Available tools: [create_calendar_event, get_calendar_events, list_accounts, send_… |
| InvokeTool | calendar_tomorrow | 1 | `invoke_tool({"serverName":"adjutant","toolName":"adjutant__get_calendar_events","arguments":"{\u0022startDate\u0022:\u00222026-09-09T00:00:00Z\u0022,\u0…)` | error: Unknown tool 'adjutant__get_calendar_events' on server 'adjutant'. Available tools: [create_calendar_event, get_calendar_events, list_accoun… |
| InvokeTool | calendar_tomorrow | 2 | `invoke_tool({"serverName":"adjutant","toolName":"adjutant__get_calendar_events","arguments":"{\u0022startTime\u0022:\u00222026-09-09T00:00:00Z\u0022,\u0…)` | error: Unknown tool 'adjutant__get_calendar_events' on server 'adjutant'. Available tools: [create_calendar_event, get_calendar_events, list_accoun… |
| InvokeTool | calendar_tomorrow | 3 | `invoke_tool({"serverName":"adjutant","toolName":"adjutant__get_calendar_events","arguments":"{\u0022startDateTime\u0022:\u00222026-09-09T00:00:00\u0022,…)` | error: Unknown tool 'adjutant__get_calendar_events' on server 'adjutant'. Available tools: [create_calendar_event, get_calendar_events, list_accoun… |
| InvokeTool | calendar_tomorrow | 4 | `invoke_tool({"serverName":"adjutant","toolName":"adjutant__get_calendar_events","arguments":"{\u0022startDateTime\u0022:\u00222026-09-09T00:00:00Z\u0022…)` | error: Unknown tool 'adjutant__get_calendar_events' on server 'adjutant'. Available tools: [create_calendar_event, get_calendar_events, list_accoun… |
| InvokeTool | calendar_tomorrow | 5 | `invoke_tool({"serverName":"adjutant","toolName":"adjutant__get_calendar_events","arguments":"{\u0022startDate\u0022:\u00222026-09-09T00:00:00Z\u0022,\u0…)` | error: Unknown tool 'adjutant__get_calendar_events' on server 'adjutant'. Available tools: [create_calendar_event, get_calendar_events, list_accoun… |
| InvokeTool | calendar_tomorrow | 6 | (never reached a downstream) |  |
| InvokeTool | calendar_tomorrow | 7 | (never reached a downstream) |  |
| InvokeTool | calendar_tomorrow | 8 | `invoke_tool({"serverName":"adjutant","toolName":"adjutant__get_calendar_events","arguments":"{\u0022startDate\u0022:\u00222026-09-09T00:00:00Z\u0022,\u0…)` | error: Unknown tool 'adjutant__get_calendar_events' on server 'adjutant'. Available tools: [create_calendar_event, get_calendar_events, list_accoun… |
| InvokeTool | calendar_tomorrow | 9 | `invoke_tool({"serverName":"adjutant","toolName":"adjutant__get_calendar_events","arguments":"{\u0022startTime\u0022:\u00222026-09-09T00:00:00Z\u0022,\u0…)` | error: Unknown tool 'adjutant__get_calendar_events' on server 'adjutant'. Available tools: [create_calendar_event, get_calendar_events, list_accoun… |
| InvokeTool | calendar_tomorrow | 10 | (never reached a downstream) | To find out what's on your calendar tomorrow, I will need to access your calendar data. Please let m… |
| InvokeTool | list_files_marimer | 1 | (never reached a downstream) |  |
| InvokeTool | list_files_marimer | 2 | `invoke_tool({"serverName":"onedrive-marimer","toolName":"list_files","arguments":null})` | error: An error occurred invoking 'list_files'. Argument mismatch for tool 'list_files' on 'onedrive-marimer'. Missing required parameter(s): [path… |
| InvokeTool | list_files_marimer | 5 | `invoke_tool({"serverName":"onedrive-marimer","toolName":"list_files","arguments":null})` | error: An error occurred invoking 'list_files'. Argument mismatch for tool 'list_files' on 'onedrive-marimer'. Missing required parameter(s): [path… |
| InvokeTool | list_files_marimer | 6 | (never reached a downstream) |  |
| InvokeTool | list_files_marimer | 7 | `invoke_tool({"serverName":"onedrive-marimer","toolName":"list_files","arguments":null})` | error: An error occurred invoking 'list_files'. Argument mismatch for tool 'list_files' on 'onedrive-marimer'. Missing required parameter(s): [path… |
| InvokeTool | list_files_marimer | 9 | (never reached a downstream) |  |
| InvokeTool | list_files_marimer | 10 | (never reached a downstream) | The MCP tools currently available do not support accessing or listing files on cloud storage service… |
| InvokeTool | list_files_personal | 1 | (never reached a downstream) |  |
| InvokeTool | list_files_personal | 2 | (never reached a downstream) |  |
| InvokeTool | list_files_personal | 3 | (never reached a downstream) |  |
| InvokeTool | list_files_personal | 4 | (never reached a downstream) |  |
| InvokeTool | list_files_personal | 5 | (never reached a downstream) |  |
| InvokeTool | list_files_personal | 6 | (never reached a downstream) |  |
| InvokeTool | list_files_personal | 7 | (never reached a downstream) |  |
| InvokeTool | list_files_personal | 8 | (never reached a downstream) |  |
| InvokeTool | list_files_personal | 9 | (never reached a downstream) |  |
| InvokeTool | list_files_personal | 10 | (never reached a downstream) |  |
| InvokeTool | docs_search | 2 | (never reached a downstream) | I am unable to access a specific Microsoft documentation on configuring the options pattern in ASP.N… |
| InvokeTool | docs_search | 3 | `invoke_tool({"serverName":"microsoft-learn","toolName":"microsoft_docs_search","arguments":"how to configure options pattern in ASP.NET Core"})` | error: An error occurred invoking 'invoke_tool'. |
| InvokeTool | docs_search | 4 | (never reached a downstream) | I couldn't find a specific document on configuring the options pattern in ASP.NET Core. However, I c… |
| InvokeTool | docs_search | 5 | `invoke_tool({"serverName":"microsoft-learn","toolName":"microsoft_docs_search","arguments":"How to configure the options pattern in ASP.NET Core"})` | error: An error occurred invoking 'invoke_tool'. |
| InvokeTool | docs_search | 6 | (never reached a downstream) | I couldn't find a specific documentation source for the options pattern in ASP.NET Core within the a… |
| InvokeTool | docs_search | 7 | (never reached a downstream) | I couldn't find a specific skill document for the Microsoft Learn Docs service, but I can provide yo… |
| InvokeTool | docs_search | 9 | `invoke_tool({"serverName":"microsoft-learn","toolName":"microsoft_docs_search","arguments":"How to configure options pattern in ASP.NET Core"})` | error: An error occurred invoking 'invoke_tool'. |
| InvokeTool | docs_search | 10 | (never reached a downstream) |  |
