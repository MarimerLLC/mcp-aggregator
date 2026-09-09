### Run: `meta-llama/llama-3.1-8b-instruct` via openai (https://openrouter.ai/api/v1), thinking off, 10 runs/task, 2026-09-08 20:53 UTC
Model info: `{"id":"meta-llama/llama-3.1-8b-instruct","canonical_slug":"meta-llama/llama-3.1-8b-instruct","hugging_face_id":"meta-llama/Meta-Llama-3.1-8B-Instruct","name":"Meta: Llama 3.1 8B Instruct","created":1721692800,"description":"Meta's latest class of model (Llama 3.1) launched with a variety of sizes & flavors. This 8B instruct-tuned version is fast and efficient. It has demonstrated strong performance compared to...","context_length":131072,"architecture":{"modality":"text->text","input_modalities":["text"],"output_modalities":["text"],"tokenizer":"Llama3","instruct_type":"llama3"},"pricing":{"prompt":"0.00000005","completion":"0.00000008","input_cache_read":"0.000000025"},"top_provider":{"context_length":131072,"max_completion_tokens":117964,"is_moderated":false},"per_request_limits":null,"supported_parameters":["frequency_penalty","logit_bias","logprobs","max_tokens","min_p","presence_penalty","repetition_penalty","response_format","seed","stop","structured_outputs","temperature","tool_choice","tools","top_k","top_logprobs","top_p"],"default_parameters":{},"supported_voices":null,"knowledge_cutoff":"2023-12-31","expiration_date":null,"links":{"details":"/api/v1/models/meta-llama/llama-3.1-8b-instruct/endpoints"},"benchmarks":{"design_arena":[],"artificial_analysis":{"intelligence_index":null,"coding_index":5.4,"agentic_index":null}}}`

#### Reliability by condition and task

| Condition | Task | Runs | First-call success | Completed | Wrong tool first | Avg steps to done | Avg turns | Avg input tok | Avg output tok | Errors |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| InvokeTool | calendar_tomorrow | 10 | 0/10 (0%) | 0/10 (0%) | 0 | - | 1.0 | 2423 | 25 | 0 |
| InvokeTool | docs_search | 10 | 0/10 (0%) | 2/10 (20%) | 9 | 5.5 | 6.0 | 22788 | 420 | 1 |
| InvokeTool | list_files_marimer | 10 | 1/10 (10%) | 1/10 (10%) | 4 | 2.0 | 2.3 | 6507 | 167 | 1 |
| InvokeTool | list_files_personal | 10 | 1/10 (10%) | 2/10 (20%) | 5 | 2.5 | 2.6 | 7355 | 143 | 2 |
| InvokeTool | send_email | 10 | 0/10 (0%) | 8/10 (80%) | 7 | 3.0 | 3.7 | 11471 | 242 | 0 |

#### Reliability by condition (all tasks)

| Condition | Runs | First-call success | Completed | Avg input tok / run | Avg output tok / run | Avg ms / run |
|---|---:|---:|---:|---:|---:|---:|
| InvokeTool | 50 | 2/50 (4%) | 13/50 (26%) | 10109 | 199 | 2092 |

#### tools/list size by condition

| Condition | Tools at start | Bytes at start | Tools at end | Bytes at end |
|---|---:|---:|---:|---:|
| InvokeTool | 13 | 7250 | 13 | 7250 |

#### First-attempt failures (what the model actually sent)

| Condition | Task | Run | First downstream call | Result |
|---|---|---:|---|---|
| InvokeTool | send_email | 1 | `invoke_tool({"arguments":"{\u0022email\u0022: \u0022alice@example.com\u0022, \u0022subject\u0022: \u0022Lunch Thursday?\u0022, \u0022body\u0022: \u0022A…)` | error: An error occurred invoking 'send_email'. Argument mismatch for tool 'send_email' on 'adjutant'. Missing required parameter(s): [to]. Unrecog… |
| InvokeTool | send_email | 2 | `invoke_tool({"arguments":"{\u0022to\u0022: \u0022alice@example.com\u0022, \u0022subject\u0022: \u0022Lunch Thursday?\u0022, \u0022body\u0022: \u0022Are …)` | error: Server 'mcp-aggregator' not found. Registered servers: [adjutant, microsoft-learn, onedrive-marimer, onedrive-personal]. Re-invoke with one … |
| InvokeTool | send_email | 3 | `invoke_tool({"arguments":"{\u0022recipients\u0022: [\u0022alice@example.com\u0022], \u0022body\u0022: \u0022Are you free for lunch on Thursday at noon?\…)` | error: An error occurred invoking 'send_email'. Argument mismatch for tool 'send_email' on 'adjutant'. Missing required parameter(s): [to]. Unrecog… |
| InvokeTool | send_email | 4 | `invoke_tool({"arguments":"{\u0022to\u0022: \u0022alice@example.com\u0022, \u0022subject\u0022: \u0022Lunch Thursday?\u0022, \u0022body\u0022: \u0022Are …)` | error: Server 'mcp-aggregator' not found. Registered servers: [adjutant, microsoft-learn, onedrive-marimer, onedrive-personal]. Re-invoke with one … |
| InvokeTool | send_email | 5 | `invoke_tool({"arguments":"{\u0022subject\u0022: \u0022Lunch Thursday?\u0022, \u0022message\u0022: \u0022Are you free for lunch on Thursday at noon?\u002…)` | error: Server 'my-email-server' not found. Registered servers: [adjutant, microsoft-learn, onedrive-marimer, onedrive-personal]. Re-invoke with one… |
| InvokeTool | send_email | 6 | `invoke_tool({"arguments":"{\u0022to\u0022: \u0022alice@example.com\u0022, \u0022subject\u0022: \u0022Lunch Thursday?\u0022, \u0022plaintext\u0022: \u002…)` | error: An error occurred invoking 'send_email'. Argument mismatch for tool 'send_email' on 'adjutant'. Missing required parameter(s): [body]. Unrec… |
| InvokeTool | send_email | 7 | `invoke_tool({"arguments":"{\u0022to\u0022: \u0022alice@example.com\u0022, \u0022subject\u0022: \u0022Lunch Thursday?\u0022, \u0022message\u0022: \u0022A…)` | error: Server 'mcp-aggregator' not found. Registered servers: [adjutant, microsoft-learn, onedrive-marimer, onedrive-personal]. Re-invoke with one … |
| InvokeTool | send_email | 8 | `invoke_tool({"arguments":"{\u0022to\u0022: \u0022alice@example.com\u0022, \u0022subject\u0022: \u0022Lunch Thursday?\u0022, \u0022message\u0022: \u0022A…)` | error: Server 'mcp-aggregator' not found. Registered servers: [adjutant, microsoft-learn, onedrive-marimer, onedrive-personal]. Re-invoke with one … |
| InvokeTool | send_email | 9 | `invoke_tool({"arguments":"{\u0022to\u0022: \u0022alice@example.com\u0022, \u0022subject\u0022: \u0022Lunch Thursday?\u0022, \u0022body\u0022: \u0022Are …)` | error: Server 'mcp-aggregator' not found. Registered servers: [adjutant, microsoft-learn, onedrive-marimer, onedrive-personal]. Re-invoke with one … |
| InvokeTool | send_email | 10 | `invoke_tool({})` | error: Argument binding failed for aggregator tool 'invoke_tool'. Missing required parameter(s): [serverName, toolName]. You sent: [(no arguments)]… |
| InvokeTool | calendar_tomorrow | 1 | (never reached a downstream) | <function>brave_search{"query": "calendar tomorrow September 9, 2026"}<function> |
| InvokeTool | calendar_tomorrow | 2 | (never reached a downstream) | <function>brave_search{"query": "my calendar tomorrow"}</function> |
| InvokeTool | calendar_tomorrow | 3 | (never reached a downstream) | <function>brave_search>{"query": "calendar Sept 9th 2026"}+</function> |
| InvokeTool | calendar_tomorrow | 4 | (never reached a downstream) | <function>brave_search {"phrase": "my calendar tomorrow"};</function> |
| InvokeTool | calendar_tomorrow | 5 | (never reached a downstream) | <function>brave_search{"query": "calendar tomorrow 2026-09-09"}<function> |
| InvokeTool | calendar_tomorrow | 6 | (never reached a downstream) | <brakelight_search {"date": "2026-09-09"}<br /> |
| InvokeTool | calendar_tomorrow | 7 | (never reached a downstream) | <function>brave_search><query>Upcoming appointments and tasks for next day in UTC time zone</query><… |
| InvokeTool | calendar_tomorrow | 8 | (never reached a downstream) | <function>brave_search {"query": "tomorrow's schedule"} </function> |
| InvokeTool | calendar_tomorrow | 9 | (never reached a downstream) | <function>brave_search{"query": "calendar 2026-09-09"};</function> |
| InvokeTool | calendar_tomorrow | 10 | (never reached a downstream) | <br><function>brave_search>{"query": "calendar for tomorrow UTC"}<br></function>

However, since you… |
| InvokeTool | list_files_marimer | 1 | `invoke_tool({})` | error: Argument binding failed for aggregator tool 'invoke_tool'. Missing required parameter(s): [serverName, toolName]. You sent: [(no arguments)]… |
| InvokeTool | list_files_marimer | 2 | (never reached a downstream) | <invoke_tool>{"serverName": "one_drive", "toolName": "sync_folder", "arguments": "{\"folder_path\": … |
| InvokeTool | list_files_marimer | 4 | (never reached a downstream) | ArgumentOutOfRangeException: Unknown ChatFinishReason value. (Parameter 'value')
Actual value was error. |
| InvokeTool | list_files_marimer | 5 | `invoke_tool({"arguments":"{\u0022path\u0022: \u0022/Documents/Reports\u0022, \u0022user_name\u0022: \u0022marimer@company.com\u0022, \u0022share_point_p…)` | error: Server 'mcp-aggregator' not found. Registered servers: [adjutant, microsoft-learn, onedrive-marimer, onedrive-personal, onedrive-report, sen… |
| InvokeTool | list_files_marimer | 6 | `invoke_tool({})` | error: Argument binding failed for aggregator tool 'invoke_tool'. Missing required parameter(s): [serverName, toolName]. You sent: [(no arguments)]… |
| InvokeTool | list_files_marimer | 7 | (never reached a downstream) | I'm not aware of any tools that would provide such information. I can suggest an alternative solutio… |
| InvokeTool | list_files_marimer | 8 | (never reached a downstream) | <invoke_tool>{"serverName": "mcp-aggregator", "toolName": "query_one_drive", "arguments": "{\"path\"… |
| InvokeTool | list_files_marimer | 9 | `invoke_tool({"arguments":"{\u0022path\u0022: \u0022{Documents}/Reports\u0022}","serverName":"onedrive-browser","toolName":"list_folder"})` | error: Server 'onedrive-browser' not found. Registered servers: [adjutant, microsoft-learn, onedrive-marimer, onedrive-personal, onedrive-report, s… |
| InvokeTool | list_files_marimer | 10 | (never reached a downstream) | <invoke_tool>{"serverName": "one-drive", "toolName": "list_files", "arguments": "{\"file_path\": \"/… |
| InvokeTool | list_files_personal | 1 | `invoke_tool({"arguments":"{\u0022folder_path\u0022: \u0022/Personal/Photos\u0022}","serverName":"OneDrive","toolName":"list_photos"})` | error: Server 'OneDrive' not found. Registered servers: [adjutant, microsoft-learn, onedrive-marimer, onedrive-personal, onedrive-report, send_emai… |
| InvokeTool | list_files_personal | 3 | `invoke_tool({"arguments":"{\u0022path\u0022:\u0022/Photos\u0022}","serverName":"onedrive-personal","toolName":"list_photos"})` | error: Unknown tool 'list_photos' on server 'onedrive-personal'. Available tools: [get_file, list_files, search_files]. Re-invoke with one of those… |
| InvokeTool | list_files_personal | 4 | `invoke_tool({"arguments":"{\u0022path\u0022: \u0022/Photos\u0022}","serverName":"one_drive","toolName":"photos_get"})` | error: Server 'one_drive' not found. Registered servers: [adjutant, microsoft-learn, onedrive-marimer, onedrive-personal, onedrive-report, send_ema… |
| InvokeTool | list_files_personal | 5 | (never reached a downstream) | I'm sorry, I can't assist with that request. |
| InvokeTool | list_files_personal | 6 | (never reached a downstream) |  |
| InvokeTool | list_files_personal | 7 | `invoke_tool({"arguments":"{\u0022directoryName\u0022: \u0022Photos\u0022}","serverName":"onedrive","toolName":"list_photos"})` | error: Server 'onedrive' not found. Registered servers: [adjutant, microsoft-learn, onedrive-marimer, onedrive-personal, onedrive-report, send_emai… |
| InvokeTool | list_files_personal | 8 | (never reached a downstream) | Unfortunately, I cannot access your personal OneDrive. To access the Photos folder you need to authe… |
| InvokeTool | list_files_personal | 9 | `invoke_tool({"arguments":"{\u0022path\u0022: \u0022/Photos\u0022}","serverName":"onedrive-marimer","toolName":"list_files"})` | ok, wrong tool |
| InvokeTool | list_files_personal | 10 | (never reached a downstream) | ArgumentOutOfRangeException: Unknown ChatFinishReason value. (Parameter 'value')
Actual value was error. |
| InvokeTool | docs_search | 1 | `invoke_tool({})` | error: Argument binding failed for aggregator tool 'invoke_tool'. Missing required parameter(s): [serverName, toolName]. You sent: [(no arguments)]… |
| InvokeTool | docs_search | 2 | `invoke_tool({})` | error: Argument binding failed for aggregator tool 'invoke_tool'. Missing required parameter(s): [serverName, toolName]. You sent: [(no arguments)]… |
| InvokeTool | docs_search | 3 | `invoke_tool({})` | error: Argument binding failed for aggregator tool 'invoke_tool'. Missing required parameter(s): [serverName, toolName]. You sent: [(no arguments)]… |
| InvokeTool | docs_search | 4 | `invoke_tool({})` | error: Argument binding failed for aggregator tool 'invoke_tool'. Missing required parameter(s): [serverName, toolName]. You sent: [(no arguments)]… |
| InvokeTool | docs_search | 5 | `invoke_tool({})` | error: Argument binding failed for aggregator tool 'invoke_tool'. Missing required parameter(s): [serverName, toolName]. You sent: [(no arguments)]… |
| InvokeTool | docs_search | 6 | `invoke_tool({})` | error: Argument binding failed for aggregator tool 'invoke_tool'. Missing required parameter(s): [serverName, toolName]. You sent: [(no arguments)]… |
| InvokeTool | docs_search | 7 | `invoke_tool({})` | error: Argument binding failed for aggregator tool 'invoke_tool'. Missing required parameter(s): [serverName, toolName]. You sent: [(no arguments)]… |
| InvokeTool | docs_search | 8 | (never reached a downstream) | <function bruce_get_service_details>{"serverName": "mcp-aggregator"};</function>

<function bruce_in… |
| InvokeTool | docs_search | 9 | `invoke_tool({})` | error: Argument binding failed for aggregator tool 'invoke_tool'. Missing required parameter(s): [serverName, toolName]. You sent: [(no arguments)]… |
| InvokeTool | docs_search | 10 | `invoke_tool({})` | error: Argument binding failed for aggregator tool 'invoke_tool'. Missing required parameter(s): [serverName, toolName]. You sent: [(no arguments)]… |
