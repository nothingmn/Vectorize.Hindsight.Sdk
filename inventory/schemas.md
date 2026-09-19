# Hindsight component schemas (spec 0.10.0)

181 schemas

### AddBackgroundRequest
_Request model for adding/merging background information. Deprecated: use SetMissionRequest instead._
- `content` **REQ** : string  New background information to add or merge
- `update_disposition` opt : boolean default=True  Deprecated - disposition is no longer auto-inferred from mission

### AsyncOperationSubmitResponse
_Response model for submitting an async operation._
- `operation_id` **REQ** : string  
- `status` **REQ** : string  

### AuditLogEntry
_A single audit log entry._
- `id` **REQ** : string  
- `action` **REQ** : string  
- `transport` **REQ** : string  
- `bank_id` **REQ** : string|null  
- `started_at` **REQ** : string|null  
- `ended_at` **REQ** : string|null  
- `duration_ms` opt : integer|null  Server-computed duration in milliseconds (started_at → ended_at). Null if not yet completed.
- `request` **REQ** : object|null  
- `response` **REQ** : object|null  
- `metadata` **REQ** : map<string,?>  

### AuditLogListResponse
_Response model for list audit logs endpoint._
- `bank_id` **REQ** : string  
- `total` **REQ** : integer  
- `limit` **REQ** : integer  
- `offset` **REQ** : integer  
- `items` **REQ** : array<AuditLogEntry>  

### AuditLogStatsBucket
_A single time bucket in audit log stats._
- `time` **REQ** : string  
- `actions` **REQ** : map<string,integer>  
- `total` **REQ** : integer  

### AuditLogStatsResponse
_Response model for audit log stats endpoint._
- `bank_id` **REQ** : string  
- `period` **REQ** : string  
- `trunc` **REQ** : string  
- `start` **REQ** : string  
- `buckets` **REQ** : array<AuditLogStatsBucket>  

### BackgroundResponse
_Response model for background update. Deprecated: use MissionResponse instead._
- `mission` **REQ** : string  
- `background` opt : string|null  Deprecated: same as mission
- `disposition` opt : DispositionTraits|null  

### BankConfigResponse
_Response model for bank configuration._
- `bank_id` **REQ** : string  Bank identifier
- `config` **REQ** : map<string,?>  Fully resolved configuration with all hierarchical overrides applied (Python field names)
- `overrides` **REQ** : map<string,?>  Bank-specific configuration overrides only (Python field names)

### BankConfigUpdate
_Request model for updating bank configuration._
- `updates` **REQ** : map<string,?>  Configuration overrides. Keys can be in Python field format (retain_extraction_mode) or environment variable format (HINDSIGHT_API

### BankListItem
_Bank list item with profile summary and stats._
- `bank_id` **REQ** : string  
- `name` opt : string|null  
- `disposition` **REQ** : DispositionTraits  
- `mission` opt : string|null  
- `created_at` opt : string|null  
- `updated_at` opt : string|null  
- `fact_count` opt : integer default=0  
- `last_document_at` opt : string|null  When a document was last *ingested* into this bank. Appending to an existing document does not move this — use `last_write_at` for
- `last_write_at` opt : string|null  When anything was last written to this bank: a document retained (including appends to an existing document) or a fact stored. Nul

### BankListResponse
_Response model for listing banks, one page at a time._
- `banks` **REQ** : array<BankListItem>  
- `total` **REQ** : integer  Total number of banks visible to the caller, ignoring `limit`/`offset`.
- `limit` **REQ** : integer  
- `offset` **REQ** : integer  

### BankLlmHealthResponse
_Per-bank LLM connectivity probe across retain/consolidation/reflect. Operations
that share a configuration are probed once. Discloses status only — never the
provider, model, endpoint, API key, or raw_
- `bank_id` **REQ** : string  Bank identifier
- `operations` **REQ** : array<LlmOperationHealth>  Connectivity status per operation (retain, consolidation, reflect)

### BankProfileResponse
_Response model for bank profile._
- `bank_id` **REQ** : string  
- `name` **REQ** : string  
- `disposition` **REQ** : DispositionTraits  
- `mission` **REQ** : string  The agent's mission - who they are and what they're trying to accomplish
- `background` opt : string|null  Deprecated: use mission instead

### BankStatsResponse
_Response model for bank statistics endpoint._
- `bank_id` **REQ** : string  
- `total_nodes` **REQ** : integer  
- `total_links` **REQ** : integer  
- `total_documents` **REQ** : integer  
- `nodes_by_fact_type` **REQ** : map<string,integer>  
- `links_by_link_type` **REQ** : map<string,integer>  
- `links_by_fact_type` **REQ** : map<string,integer>  
- `links_breakdown` **REQ** : map<string,object>  
- `pending_operations` **REQ** : integer  
- `failed_operations` **REQ** : integer  
- `operations_by_status` opt : map<string,integer>  Async operations grouped by status (pending, processing, completed, failed, cancelled).
- `last_consolidated_at` opt : string|null  When consolidation last ran (ISO format)
- `last_memory_write_at` opt : string|null  When a memory was last written in this bank — stored, edited, or consolidated (ISO format). Null if the bank has no memories. A me
- `pending_consolidation` opt : integer default=0  Number of source memories (world/experience) still queued for consolidation into observations. Excludes memories whose consolidati
- `failed_consolidation` opt : integer default=0  Number of source memories (world/experience) whose consolidation permanently failed and can be retried via the consolidation recov
- `total_observations` opt : integer default=0  Total number of observations

### BankTemplateConfig
_Bank configuration fields within a template manifest.

Only includes configurable (per-bank) fields. Credential fields
(API keys, base URLs) are intentionally excluded for security._
- `reflect_mission` opt : string|null  Mission/context for Reflect operations
- `retain_mission` opt : string|null  Steers what gets extracted during retain
- `retain_extraction_mode` opt : string|null  Fact extraction mode: 'concise' (default), 'verbose', 'custom', 'verbatim', or 'chunks'
- `retain_custom_instructions` opt : string|null  Custom extraction prompt (when mode='custom')
- `retain_chunk_size` opt : integer|null  Target max characters for each content chunk
- `retain_structured_chunk_size` opt : integer|null  Max characters for a single JSONL line or conversation turn to keep whole; defaults to retain_chunk_size when unset
- `enable_observations` opt : boolean|null  Toggle observation consolidation
- `observations_mission` opt : string|null  Controls what gets synthesised
- `enable_text_search` opt : boolean|null  Toggle the keyword (BM25) arm during recall, leaving pure vector search
- `enable_temporal_retrieval` opt : boolean|null  Toggle the temporal arm (and its date-aware query analysis) during recall
- `enable_graph_retrieval` opt : boolean|null  Toggle the entity/link graph arm during recall
- `enable_reranking` opt : boolean|null  Toggle cross-encoder reranking during recall
- `disposition_skepticism` opt : integer|null  Skepticism trait (1-5)
- `disposition_literalism` opt : integer|null  Literalism trait (1-5)
- `disposition_empathy` opt : integer|null  Empathy trait (1-5)
- `entity_labels` opt : array|null  Controlled vocabulary for entity labels
- `entities_allow_free_form` opt : boolean|null  Allow entities outside the label vocabulary
- `retain_default_strategy` opt : string|null  Name of the default retain strategy (key into retain_strategies map)
- `retain_strategies` opt : object|null  Map of retain strategy name to per-strategy config dict
- `retain_chunk_batch_size` opt : integer|null  Max chunks per streaming batch (0 disables batching)
- `retain_max_attachments_per_chunk` opt : integer|null  Hard cap on inline images in a single extraction chunk
- `mcp_enabled_tools` opt : array|null  MCP tool allowlist for this bank (None = all tools)
- `consolidation_llm_batch_size` opt : integer|null  LLM batch size for observation consolidation
- `consolidation_source_facts_max_tokens` opt : integer|null  Max tokens of source facts per consolidation batch
- `consolidation_source_facts_max_tokens_per_observation` opt : integer|null  Max tokens of source facts per observation
- `max_observations_per_scope` opt : integer|null  Max observations to retain per consolidation scope
- `observation_scope_limits` opt : array|null  Per-scope overrides of max_observations_per_scope: [{"scope": ["run_*", "shared"], "limit": 1}]. Each scope is a list of fnmatch t
- `reflect_source_facts_max_tokens` opt : integer|null  Max tokens of source facts per reflect call
- `knowledge_page_default_trigger` opt : object|null  Trigger fields merged over the built-in knowledge-page default when a page is created (e.g. {"refresh_cron": "0 * * * *"}). A trig
- `reflect_default_options` opt : object|null  Default reflect options for this bank (e.g. {"reflect_search_observations_max_tokens": 3000, "reflect_search_observations_include_
- `mental_model_min_refresh_interval_seconds` opt : integer|null  Minimum seconds between two automatic refreshes of the same mental model in this bank. 0 (the default) means no floor. Overridable
- `llm_gemini_safety_settings` opt : array|null  Per-bank Gemini/VertexAI safety filter settings
- `recall_budget_function` opt : string|null  Recall budget mapping function: 'fixed' or 'adaptive'
- `recall_budget_fixed_low` opt : integer|null  Fixed thinking_budget for budget=low (function='fixed')
- `recall_budget_fixed_mid` opt : integer|null  Fixed thinking_budget for budget=mid (function='fixed')
- `recall_budget_fixed_high` opt : integer|null  Fixed thinking_budget for budget=high (function='fixed')
- `recall_budget_adaptive_low` opt : number|null  Ratio of max_tokens for budget=low (function='adaptive')
- `recall_budget_adaptive_mid` opt : number|null  Ratio of max_tokens for budget=mid (function='adaptive')
- `recall_budget_adaptive_high` opt : number|null  Ratio of max_tokens for budget=high (function='adaptive')
- `recall_budget_min` opt : integer|null  Floor for the adaptive function (after clamping)
- `recall_budget_max` opt : integer|null  Ceiling for the adaptive function (after clamping)
- `audit_log_enabled` opt : boolean|null  Enable audit logging for this bank (overrides the server default)
- `store_document_text` opt : boolean|null  Persist raw source text (documents.original_text / chunks.chunk_text). Set false to keep only derived facts.
- `enable_auto_consolidation` opt : boolean|null  Automatically consolidate observations after retain
- `consolidation_max_memories_per_round` opt : integer|null  Max memory units fed into a single consolidation round
- `consolidation_llm_parallelism` opt : integer|null  Number of consolidation LLM batches processed concurrently
- `recall_include_chunks` opt : boolean|null  Include raw chunks in recall results
- `recall_max_tokens` opt : integer|null  Max tokens of results returned by recall
- `recall_chunks_max_tokens` opt : integer|null  Max tokens of raw chunks returned by recall (when recall_include_chunks is set)
- `memory_defense` opt : object|null  Memory Defense policy for this bank (validated against the DefensePolicy schema on write)

### BankTemplateDirective
_A directive definition within a bank template manifest.

Directives are matched by name on re-import: existing directives
with the same name are updated, new ones are created._
- `name` **REQ** : string  Human-readable name for the directive (used as match key on re-import)
- `content` **REQ** : string  The directive text to inject into prompts
- `priority` opt : integer default=0  Higher priority directives are injected first
- `is_active` opt : boolean default=True  Whether this directive is active
- `tags` opt : array<string> default=[]  Tags for filtering

### BankTemplateImportResponse
_Response model for the bank template import endpoint._
- `bank_id` **REQ** : string  Bank that was imported into
- `config_applied` **REQ** : boolean  Whether bank config was updated
- `mental_models_created` opt : array<string> default=[]  IDs of newly created mental models
- `mental_models_updated` opt : array<string> default=[]  IDs of updated mental models
- `directives_created` opt : array<string> default=[]  Names of newly created directives
- `directives_updated` opt : array<string> default=[]  Names of updated directives
- `operation_ids` opt : array<string> default=[]  Operation IDs for mental model content generation (async)
- `dry_run` opt : boolean default=False  True if this was a validation-only run

### BankTemplateManifest
_A bank template manifest for import/export.

Version field enables forward-compatible schema evolution: the API
auto-upgrades older manifest versions to the current schema on import._
- `version` **REQ** : string  Manifest schema version (currently '1')
- `bank` opt : BankTemplateConfig|null  Bank configuration to apply. Omit to leave config unchanged.
- `mental_models` opt : array|null  Mental models to create or update (matched by id). Omit to leave unchanged.
- `directives` opt : array|null  Directives to create or update (matched by name). Omit to leave unchanged.

### BankTemplateMentalModel
_A mental model definition within a bank template manifest._
- `id` **REQ** : string  Unique ID for the mental model (alphanumeric lowercase with hyphens)
- `name` **REQ** : string  Human-readable name for the mental model
- `source_query` **REQ** : string  The query to run to generate content
- `tags` opt : array<string> default=[]  Tags for scoped visibility
- `max_tokens` opt : integer default=2048  Maximum tokens for generated content
- `trigger` opt : MentalModelTrigger-Output default={}  Trigger settings

### BankTransferSubmitResponse
_Response for the unified bank-transfer endpoints (202).

The transfer runs in the background; poll
GET /v1/default/banks/{bank_id}/operations/{operation_id}. An export's
``result_metadata`` carries ``_
- `operation_id` **REQ** : string  
- `status` opt : string default='pending'  

### Base64AttachmentSource
_Inline attachment bytes, base64-encoded.

The only source type in this version. ``url`` (server-side fetch) and
``blob_id`` (pre-uploaded handle) are the natural next ones, which is why this
is modell_
- `type` opt : string default='base64'  
- `media_type` **REQ** : string  MIME type of the attachment, e.g. 'image/png' or 'application/pdf'. Any well-formed type is accepted; whether the model can read i
- `data` **REQ** : string  Base64-encoded bytes (no data: URI prefix).

### Body_file_retain
- `files` **REQ** : array<string>  Files to upload and convert
- `request` **REQ** : string  JSON string with FileRetainRequest model

### Body_import_bank_transfer
- `file` **REQ** : string  Transfer ZIP archive

### Body_import_documents
- `file` **REQ** : string  Transfer ZIP archive

### Budget
_Budget levels for recall/reflect operations._
- shape: {"type": "string", "enum": ["low", "mid", "high"], "title": "Budget", "description": "Budget levels for recall/reflect operations."}

### CancelOperationResponse
_Response model for cancel operation endpoint._
- `success` **REQ** : boolean  
- `message` **REQ** : string  
- `operation_id` **REQ** : string  

### ChildOperationStatus
_Status of a child operation (for batch operations)._
- `operation_id` **REQ** : string  
- `status` **REQ** : string  
- `sub_batch_index` opt : integer|null  
- `items_count` opt : integer|null  
- `error_message` opt : string|null  

### ChunkAttachment
_An attachment referenced by retained text, and where to fetch it._
- `id` **REQ** : string  The id inside the text's placeholder; a prefix of the bytes' sha256.
- `hash` **REQ** : string  Full sha256 of the attachment bytes.
- `kind` **REQ** : string  'image' or 'file', as the caller sent it.
- `media_type` **REQ** : string  MIME type of the attachment.
- `byte_size` **REQ** : integer  Size of the attachment in bytes.
- `filename` opt : string|null  Original filename, when the caller supplied one.
- `url` **REQ** : string  Bank-scoped API path serving the bytes. Requires the same authorization as the bank.

### ChunkData
_Chunk data for a single chunk._
- `id` **REQ** : string  
- `text` **REQ** : string  
- `chunk_index` **REQ** : integer  
- `truncated` opt : boolean default=False  Whether the chunk text was truncated due to token limits
- `attachments` opt : array|null  Attachments this chunk's text references, in order of first appearance, when it was retained with inline content. The text keeps e

### ChunkIncludeOptions
_Options for including chunks in recall results._
- `max_tokens` opt : integer default=8192  Maximum tokens for chunks (chunks may be truncated)

### ChunkResponse
_Response model for get chunk endpoint._
- `chunk_id` **REQ** : string  
- `document_id` **REQ** : string  
- `bank_id` **REQ** : string  
- `chunk_index` **REQ** : integer  
- `chunk_text` **REQ** : string  
- `created_at` **REQ** : string  
- `attachments` opt : array|null  Attachments referenced by this chunk's text, when it was retained with inline content. Each carries a bank-scoped `url` serving th

### ClearMemoryObservationsResponse
_Response model for clearing observations for a specific memory._
- `deleted_count` **REQ** : integer  

### ConsolidationRequest
_Request model for consolidation trigger endpoint._
- `observation_scopes` opt : array|null  Optional list of tag scopes to consolidate. Each scope is a list of tags. Only unconsolidated memories whose tags contain all tags

### ConsolidationResponse
_Response model for consolidation trigger endpoint._
- `operation_id` **REQ** : string  ID of the async consolidation operation
- `deduplicated` opt : boolean default=False  True if an existing pending task was reused

### CreateBankRequest
_Request model for creating/updating a bank._
- `name` opt : string|null  Deprecated: display label only, not advertised
- `disposition` opt : DispositionTraits|null  Deprecated: use update_bank_config instead
- `disposition_skepticism` opt : integer|null  Deprecated: use update_bank_config instead
- `disposition_literalism` opt : integer|null  Deprecated: use update_bank_config instead
- `disposition_empathy` opt : integer|null  Deprecated: use update_bank_config instead
- `mission` opt : string|null  Deprecated: use update_bank_config with reflect_mission instead
- `background` opt : string|null  Deprecated: use update_bank_config with reflect_mission instead
- `reflect_mission` opt : string|null  Mission/context for Reflect operations. Guides how Reflect interprets and uses memories.
- `retain_mission` opt : string|null  Steers what gets extracted during retain(). Injected alongside built-in extraction rules.
- `retain_extraction_mode` opt : string|null  Fact extraction mode: 'concise' (default), 'verbose', 'custom', 'verbatim', or 'chunks'.
- `retain_custom_instructions` opt : string|null  Custom extraction prompt. Only active when retain_extraction_mode is 'custom'.
- `retain_chunk_size` opt : integer|null  Target maximum characters for each content chunk during retain.
- `retain_structured_chunk_size` opt : integer|null  Maximum characters for a single JSONL line or conversation turn to keep whole during retain. Defaults to retain_chunk_size when un
- `retain_max_attachments_per_chunk` opt : integer|null  Maximum inline attachments one extraction chunk may carry. retain_chunk_size budgets text only — a placeholder costs the character
- `enable_observations` opt : boolean|null  Toggle automatic observation consolidation after retain().
- `observations_mission` opt : string|null  Controls what gets synthesised into observations. Replaces built-in consolidation rules entirely.
- `enable_text_search` opt : boolean|null  Toggle the keyword (BM25) retrieval arm during recall. Disabling leaves pure vector search: the arm is left out of the query entir
- `enable_temporal_retrieval` opt : boolean|null  Toggle the temporal retrieval arm during recall, together with the date-aware query analysis that feeds it. Useful for banks whose
- `enable_graph_retrieval` opt : boolean|null  Toggle the entity/link graph traversal arm during recall. Disabling trades relational recall for latency on banks whose content ha
- `enable_reranking` opt : boolean|null  Toggle cross-encoder reranking during recall. Disabling returns the RRF-fused ordering directly, which is faster but less precise.

### CreateDirectiveRequest
_Request model for creating a directive._
- `name` **REQ** : string  Human-readable name for the directive
- `content` **REQ** : string  The directive text to inject into prompts
- `priority` opt : integer default=0  Higher priority directives are injected first
- `is_active` opt : boolean default=True  Whether this directive is active
- `tags` opt : array<string> default=[]  Directive execution scope. Empty means global; non-empty requires a matching reflect scope.

### CreateFolderRequest
_Create a folder under an optional parent folder._
- `name` **REQ** : string  
- `parent_id` opt : string|null  

### CreateKnowledgePageResponse
_Result of creating a page: the node id, its mental model, and the refresh op._
- `page_id` **REQ** : string  
- `mental_model_id` **REQ** : string  
- `operation_id` opt : string|null  

### CreateMentalModelRequest
_Request model for creating a mental model._
- `id` opt : string|null  Optional custom ID for the mental model (alphanumeric lowercase with hyphens)
- `name` **REQ** : string  Human-readable name for the mental model
- `source_query` **REQ** : string  The query to run to generate content
- `tags` opt : array<string> default=[]  Tags for scoped visibility
- `max_tokens` opt : integer default=2048  Maximum tokens for generated content
- `trigger` opt : MentalModelTrigger-Input default={}  Trigger settings

### CreateMentalModelResponse
_Response model for mental model creation._
- `mental_model_id` opt : string|null  ID of the created mental model
- `operation_id` **REQ** : string  Operation ID to track refresh progress

### CreatePageRequest
_Create a page (a mental model + tree node) under an optional parent folder._
- `name` **REQ** : string  
- `source_query` **REQ** : string  
- `parent_id` opt : string|null  
- `tags` opt : array|null  Tags that SCOPE which memories this page is built from — not labels. Every tag here, including a `type:<x>` tag used to set the pa
- `max_tokens` opt : integer|null  
- `trigger` opt : MentalModelTrigger-Input|null  

### CreateWebhookRequest
_Request model for registering a webhook._
- `url` **REQ** : string  HTTP(S) endpoint URL to deliver events to
- `secret` opt : string|null  HMAC-SHA256 signing secret (optional)
- `event_types` opt : array<string> default=['consolidation.completed']  List of event types to deliver. Supported: 'retain.completed', 'consolidation.completed', 'memory_defense.triggered'.
- `enabled` opt : boolean default=True  Whether this webhook is active
- `http_config` opt : WebhookHttpConfig  HTTP delivery configuration (method, timeout, headers, params)

### DeleteDocumentResponse
_Response model for delete document endpoint._
- `success` **REQ** : boolean  
- `message` **REQ** : string  
- `document_id` **REQ** : string  
- `memory_units_deleted` **REQ** : integer  

### DeleteOperationResponse
_Response model for delete operation endpoint._
- `success` **REQ** : boolean  
- `message` **REQ** : string  
- `operation_id` **REQ** : string  

### DeleteResponse
_Response model for delete operations._
- `success` **REQ** : boolean  
- `message` opt : string|null  
- `deleted_count` opt : integer|null  

### DirectiveListResponse
_Response model for listing directives._
- `items` **REQ** : array<DirectiveResponse>  
- `total` **REQ** : integer  Total number of directives matching the filter (not just this page)
- `limit` **REQ** : integer  Page size that was applied
- `offset` **REQ** : integer  Offset that was applied

### DirectiveResponse
_Response model for a directive._
- `id` **REQ** : string  
- `bank_id` **REQ** : string  
- `name` **REQ** : string  
- `content` **REQ** : string  
- `priority` opt : integer default=0  
- `is_active` opt : boolean default=True  
- `tags` opt : array<string> default=[]  
- `created_at` opt : string|null  
- `updated_at` opt : string|null  

### DispositionTraits
_Disposition traits that influence how memories are formed and interpreted._
- `skepticism` **REQ** : integer  How skeptical vs trusting (1=trusting, 5=skeptical)
- `literalism` **REQ** : integer  How literally to interpret information (1=flexible, 5=literal)
- `empathy` **REQ** : integer  How much to consider emotional context (1=detached, 5=empathetic)

### DocumentExportSubmitResponse
_Response for the async document-export endpoint (202).

The export runs in the background; poll the operations endpoint for status.
On completion the operation's ``result_metadata`` carries ``download_
- `operation_id` **REQ** : string  
- `status` opt : string default='pending'  

### DocumentImportSubmitResponse
_Response for the async document-import endpoint (202).

The import runs in the background; poll the operations endpoint for status.
The imported/skipped counts (documents_imported, facts_imported,
obs_
- `operation_id` **REQ** : string  
- `status` opt : string default='pending'  

### DocumentListItem
_One row of the document listing — a document's metadata without its text.

Extra keys are allowed and passed through: the rows used to be an open object, and
typing them must not drop a field an older_
- `id` **REQ** : string  Document ID
- `bank_id` opt : string default=''  Bank the document belongs to
- `content_hash` opt : string|null  Hash of the document text, for idempotent retain
- `created_at` opt : string default=''  When the document was first retained (ISO 8601)
- `updated_at` opt : string default=''  When the document was last written (ISO 8601)
- `text_length` opt : integer default=0  Length of the stored document text in characters
- `memory_unit_count` opt : integer default=0  Number of memory units extracted from this document
- `retain_params` opt : object|null  Parameters used during retain
- `document_metadata` opt : object|null  Document metadata
- `tags` opt : array<string> default=[]  Tags associated with this document

### DocumentResponse
_Response model for get document endpoint._
- `id` **REQ** : string  
- `bank_id` **REQ** : string  
- `original_text` **REQ** : string|null  
- `content_hash` **REQ** : string|null  
- `created_at` **REQ** : string  
- `updated_at` **REQ** : string  
- `memory_unit_count` **REQ** : integer  
- `nodes_by_fact_type` opt : object|null  Memory count per fact type (world, experience, observation)
- `tags` opt : array<string> default=[]  Tags associated with this document
- `document_metadata` opt : object|null  Document metadata
- `retain_params` opt : object|null  Parameters used during retain
- `observation_scopes` opt : string|array|null  The observation_scopes spec configured at retain time (e.g. 'all_combinations', 'per_tag', or explicit tag-set lists), captured in
- `attachments` opt : array|null  Attachments referenced by this document, when it was retained with inline content. Each carries a bank-scoped `url` serving the or

### DryRunExtractRequest
_Request to run fact extraction ONLY (no resolution/links/embeddings/persistence).

Every field below the content/context/date is a prompt-affecting override applied just for this
call — used to previe_
- `content` **REQ** : string  Text to extract facts from (e.g. a document or a single chunk).
- `context` opt : string default=''  Optional context about the content.
- `timestamp` opt : string|null  Reference timestamp for resolving relative times (ISO 8601).
- `agent_name` opt : string|null  Deprecated: describe the speaker in `context` instead. Narrator override (memory owner) primed in the prompt; still honored for ba
- `strategy` opt : string|null  Name of a retain strategy to extract under (a key of the bank's `retain_strategies`). Omit it and the bank's `retain_default_strat
- `retain_mission` opt : string|null  
- `retain_extraction_mode` opt : string|null  
- `retain_custom_instructions` opt : string|null  
- `retain_extract_causal_links` opt : boolean|null  
- `retain_chunk_size` opt : integer|null  
- `entity_labels` opt : array|null  Controlled vocabulary for entity labels (overrides the bank's config for this call)
- `entities_allow_free_form` opt : boolean|null  
- `llm_output_language` opt : string|null  

### DryRunExtractionResult
_Result of dry-run fact extraction: candidate facts, the chunks they came from,
and aggregated LLM token usage._
- `facts` opt : array<ExtractedFact>  Candidate facts the retain step would extract.
- `chunks` opt : array<ExtractionChunk>  The chunks the input was cut into before extraction. Already computed on every path; returned because `retain_chunk_size` is other
- `usage` opt : TokenUsage  Aggregated token usage across the extraction LLM calls.

### EntityDetailResponse
_Response model for entity detail endpoint._
- `id` **REQ** : string  
- `canonical_name` **REQ** : string  
- `mention_count` **REQ** : integer  
- `first_seen` opt : string|null  
- `last_seen` opt : string|null  
- `metadata` opt : object|null  
- `observations` **REQ** : array<EntityObservationResponse>  

### EntityGraphEdge
_A co-occurrence edge, in the Cytoscape ``{"data": {...}}`` envelope the graph uses._
- `data` **REQ** : EntityGraphEdgeData  

### EntityGraphEdgeData
_The payload of one co-occurrence edge._
- `id` **REQ** : string  Edge ID (``<source>-<target>``)
- `source` **REQ** : string  Source entity ID
- `target` **REQ** : string  Target entity ID
- `linkType` opt : string default='cooccurrence'  Kind of relationship this edge represents
- `weight` opt : integer default=0  Number of co-occurrences between the two entities
- `color` opt : string|null  Suggested edge colour for rendering
- `lineStyle` opt : string|null  Suggested edge line style for rendering
- `lastCooccurred` opt : string|null  ISO 8601 timestamp of the most recent co-occurrence

### EntityGraphNode
_An entity node, in the Cytoscape ``{"data": {...}}`` envelope the graph uses._
- `data` **REQ** : EntityGraphNodeData  

### EntityGraphNodeData
_The payload of one entity node in the co-occurrence graph.

Extra keys are allowed and passed through: the graph payload has always been an open
object, and typing it must not drop a field an older or_
- `id` **REQ** : string  Entity ID
- `label` opt : string default=''  Entity canonical name
- `mentionCount` opt : integer default=0  How many times this entity was mentioned
- `color` opt : string|null  Suggested node colour for rendering

### EntityGraphResponse
_Response model for entity co-occurrence graph endpoint._
- `nodes` **REQ** : array<EntityGraphNode>  
- `edges` **REQ** : array<EntityGraphEdge>  
- `total_entities` **REQ** : integer  
- `total_edges` **REQ** : integer  
- `limit` **REQ** : integer  

### EntityIncludeOptions
_Options for including entity observations in recall results._
- `max_tokens` opt : integer default=500  Maximum tokens for entity observations

### EntityInput
_Entity to associate with retained content._
- `text` **REQ** : string  The entity name/text
- `type` opt : string|null  Optional entity type (e.g., 'PERSON', 'ORG', 'CONCEPT')

### EntityListItem
_Entity list item with summary._
- `id` **REQ** : string  
- `canonical_name` **REQ** : string  
- `mention_count` **REQ** : integer  
- `first_seen` opt : string|null  
- `last_seen` opt : string|null  
- `metadata` opt : object|null  

### EntityListResponse
_Response model for entity list endpoint._
- `items` **REQ** : array<EntityListItem>  
- `total` **REQ** : integer  
- `limit` **REQ** : integer  
- `offset` **REQ** : integer  

### EntityObservationResponse
_An observation about an entity._
- `text` **REQ** : string  
- `mentioned_at` opt : string|null  

### EntityStateResponse
_Current mental model of an entity._
- `entity_id` **REQ** : string  
- `canonical_name` **REQ** : string  
- `observations` **REQ** : array<EntityObservationResponse>  

### ExtractedFact
_A single candidate fact produced by dry-run extraction (no resolution/links/persistence).

A deliberate subset of the persisted memory-unit shape — only the fields a fresh extraction
yields. Storage/c_
- `text` **REQ** : string  The extracted fact text.
- `fact_type` **REQ** : string  Perspective classification: 'world' or 'experience'.
- `occurred_start` opt : string|null  ISO timestamp the fact's event started, if dated.
- `occurred_end` opt : string|null  ISO timestamp the fact's event ended, if dated.
- `entities` opt : array<string>  Raw (unresolved) entity names mentioned in the fact.
- `chunk_index` opt : integer|null  Index into `chunks` of the chunk this fact came from; null if it could not be attributed.

### ExtractionChunk
_One chunk the extractor was handed, and how much it yielded._
- `text` **REQ** : string  The chunk as the extractor saw it.
- `fact_count` **REQ** : integer  How many facts came out of this chunk.

### FactsIncludeOptions
_Options for including facts (based_on) in reflect results._
- shape: {"properties": {}, "type": "object", "title": "FactsIncludeOptions", "description": "Options for including facts (based_on) in reflect results."}

### FeaturesInfo
_Feature flags indicating which capabilities are enabled._
- `observations` **REQ** : boolean  Whether observations (auto-consolidation) are enabled
- `mcp` **REQ** : boolean  Whether MCP (Model Context Protocol) server is enabled
- `worker` **REQ** : boolean  Whether the background worker is enabled
- `bank_config_api` **REQ** : boolean  Whether per-bank configuration API is enabled
- `bank_llm_health` **REQ** : boolean  Whether the per-bank LLM connectivity probe is enabled
- `file_upload_api` **REQ** : boolean  Whether file upload/conversion API is enabled
- `document_export_api` **REQ** : boolean  Whether the document export endpoint is enabled
- `document_import_api` **REQ** : boolean  Whether the document import endpoint is enabled
- `audit_log` **REQ** : boolean  Whether audit logging is enabled by default (overridable per bank)
- `llm_trace` **REQ** : boolean  Whether per-bank LLM request tracing is enabled
- `store_document_text` **REQ** : boolean  Whether raw source text is persisted. When false, document/chunk source text is not stored.

### FileContentBlock
_A non-image attachment — a PDF, a spreadsheet — in the position it was written.

Split from ``image`` rather than folded into one type because the providers
split it: Anthropic has distinct image and _
- `type` **REQ** : string  
- `source` **REQ** : Base64AttachmentSource  
- `filename` opt : string|null  Original filename, passed to providers that show one to the model (e.g. OpenAI).

### FileRetainResponse
_Response model for file upload endpoint._
- `operation_ids` **REQ** : array<string>  Operation IDs for tracking file conversion operations. Use GET /v1/default/banks/{bank_id}/operations to list operations.

### GraphDataResponse
_Response model for graph data endpoint._
- `nodes` **REQ** : array<MemoryGraphNode>  
- `edges` **REQ** : array<MemoryGraphEdge>  
- `table_rows` **REQ** : array<MemoryGraphTableRow>  
- `total_units` **REQ** : integer  
- `limit` **REQ** : integer  

### HTTPValidationError
- `detail` opt : array<ValidationError>  

### ImageContentBlock
_An image within a multimodal item, in the position the caller wrote it._
- `type` **REQ** : string  
- `source` **REQ** : Base64AttachmentSource  

### IncludeOptions
_Options for including additional data in recall results._
- `entities` opt : EntityIncludeOptions|null default={'max_tokens': 500}  Include entity observations. Set to null to disable entity inclusion.
- `chunks` opt : ChunkIncludeOptions|null  Include raw chunks. Set to {} to enable, null to disable (default: disabled).
- `source_facts` opt : SourceFactsIncludeOptions|null  Include source facts for observation-type results. Set to {} to enable, null to disable (default: disabled).

### KnowledgeNode
_A node in the knowledge-base tree — a folder or a page.

Pages carry ``description``/``tags`` from their backing mental model. The
knowledge base is client-managed (CRUD); ``managed`` lets a client ta_
- `id` **REQ** : string  
- `kind` **REQ** : enum(string)  
- `name` **REQ** : string  
- `parent_id` opt : string|null  
- `mental_model_id` opt : string|null  Backing mental model id (pages only).
- `managed` opt : boolean default=False  Client-set flag: true = system-owned, false = hand-authored.
- `description` opt : string|null  Page source query (the page's `description`).
- `tags` opt : array<string> default=[]  
- `timestamp` opt : string|null  Last refresh (page) or last update (folder).
- `is_stale` opt : boolean|null  Pages only, populated by the tree endpoint. True when a memory in *this page's* scope — its tags and fact types — has been written
- `trigger` opt : MentalModelTrigger-Output|null  Pages only: the page's refresh settings — when it rebuilds itself (`refresh_after_consolidation` or `refresh_cron`), in which mode
- `children` opt : array<KnowledgeNode> default=[]  

### KnowledgePageBundleFile
_One file in a portable markdown bundle._
- `path` **REQ** : string  
- `content` **REQ** : string  

### KnowledgePageBundleResponse
_A portable markdown bundle — a flat set of markdown files (index + pages + logs)._
- `files` **REQ** : array<KnowledgePageBundleFile>  

### KnowledgePageResponse
_A knowledge page rendered as a markdown document._
- `id` **REQ** : string  
- `name` **REQ** : string  
- `type` **REQ** : string  Page type — from a `type:<x>` tag, else 'knowledge-page'.
- `description` opt : string|null  The source query that rebuilds the page.
- `tags` opt : array<string> default=[]  
- `timestamp` opt : string|null  Last refresh time (falls back to creation).
- `body` opt : string|null  The page's synthesized markdown body.
- `markdown` **REQ** : string  The full markdown document: YAML frontmatter + markdown body.

### KnowledgePageSearchResponse
_Ranked knowledge-page search results (BM25 + vector, RRF-fused)._
- `results` **REQ** : array<KnowledgePageSearchResult>  
- `total` **REQ** : integer  

### KnowledgePageSearchResult
_One hybrid-search hit: a knowledge page and its fused relevance score._
- `id` **REQ** : string  
- `name` **REQ** : string  
- `mental_model_id` opt : string|null  
- `snippet` **REQ** : string  
- `score` **REQ** : number  Rank-fusion score in 0..1, where 1.0 means every search arm placed this page first. It reflects where the page ranked for this que
- `updated_at` opt : string|null  

### KnowledgeTreeResponse
_The knowledge base as a nested folder/page tree._
- `roots` **REQ** : array<KnowledgeNode>  

### LLMCallTrace
_A single LLM call made during reflect._
- `scope` **REQ** : string  Call scope: agent_1, agent_2, final, etc.
- `duration_ms` **REQ** : integer  Execution time in milliseconds

### LLMRequestEntry
_A single LLM request trace row, as returned by the read API._
- `id` **REQ** : string  
- `bank_id` **REQ** : string|null  
- `operation` **REQ** : string|null  
- `scope` **REQ** : string|null  
- `trace_id` **REQ** : string|null  
- `span_id` **REQ** : string|null  
- `parent_span_id` **REQ** : string|null  
- `provider` **REQ** : string|null  
- `model` **REQ** : string|null  
- `status` **REQ** : string  
- `started_at` **REQ** : string|null  
- `ended_at` **REQ** : string|null  
- `duration_ms` **REQ** : integer|null  
- `input_tokens` **REQ** : integer|null  
- `output_tokens` **REQ** : integer|null  
- `cached_tokens` **REQ** : integer|null  
- `total_tokens` **REQ** : integer|null  
- `input` opt : ?  
- `output` opt : ?  
- `error` **REQ** : string|null  
- `llm_info` **REQ** : map<string,?>  
- `metadata` **REQ** : map<string,?>  

### LLMRequestListResponse
_Paginated list of LLM request traces for a bank._
- `bank_id` **REQ** : string  
- `total` **REQ** : integer  
- `limit` **REQ** : integer  
- `offset` **REQ** : integer  
- `items` **REQ** : array<LLMRequestEntry>  

### LLMRequestStatsBucket
_A single time bucket in LLM request stats._
- `time` **REQ** : string  
- `statuses` **REQ** : map<string,integer>  
- `total` **REQ** : integer  
- `tokens` **REQ** : LLMRequestTokenSums  

### LLMRequestStatsResponse
_LLM request counts and token sums grouped by time bucket._
- `bank_id` **REQ** : string  
- `period` **REQ** : string  
- `trunc` **REQ** : string  
- `start` **REQ** : string  
- `buckets` **REQ** : array<LLMRequestStatsBucket>  

### LLMRequestTokenSums
_Token totals for a time bucket._
- `input` **REQ** : integer  
- `output` **REQ** : integer  
- `cached` **REQ** : integer  
- `total` **REQ** : integer  

### LabelGroup-Input
_A label group (dimension) with its type and allowed values._
- `key` **REQ** : string  
- `description` opt : string default=''  
- `type` opt : enum(string) default='value'  
- `optional` opt : boolean default=True  
- `tag` opt : boolean default=False  
- `values` opt : array<LabelValue> default=[]  
- `fields` opt : map<string,MapField-Input> default={}  

### LabelGroup-Output
_A label group (dimension) with its type and allowed values._
- `key` **REQ** : string  
- `description` opt : string default=''  
- `type` opt : enum(string) default='value'  
- `optional` opt : boolean default=True  
- `tag` opt : boolean default=False  
- `values` opt : array<LabelValue> default=[]  
- `fields` opt : map<string,MapField-Output> default={}  

### LabelValue
_A single allowed value for a label group._
- `value` **REQ** : string  
- `description` opt : string default=''  

### ListChunksResponse
_Response model for listing chunks of a document._
- `items` **REQ** : array<ChunkResponse>  
- `total` **REQ** : integer  
- `limit` **REQ** : integer  
- `offset` **REQ** : integer  

### ListDocumentsResponse
_Response model for list documents endpoint._
- `items` **REQ** : array<DocumentListItem>  
- `total` **REQ** : integer  
- `limit` **REQ** : integer  
- `offset` **REQ** : integer  

### ListMemoryUnitsResponse
_Response model for list memory units endpoint._
- `items` **REQ** : array<MemoryUnitListItem>  
- `total` **REQ** : integer  
- `limit` **REQ** : integer  
- `offset` **REQ** : integer  

### ListTagsResponse
_Response model for list tags endpoint._
- `items` **REQ** : array<TagItem>  
- `total` **REQ** : integer  
- `limit` **REQ** : integer  
- `offset` **REQ** : integer  

### LivenessResponse
_Payload for the API's DB-free liveness probe._
- `status` **REQ** : string  Always "alive" — reaching this handler is the check
- `version` **REQ** : string  Hindsight version this process is running
- `uptime_seconds` **REQ** : number  Seconds since the process started

### LlmOperationHealth
_LLM connectivity status for a single operation. Status only — no provider/model/
endpoint/error, so the probe never discloses the LLM configuration._
- `operation` **REQ** : enum(string)  Operation whose LLM was probed
- `ok` **REQ** : boolean  True only when the probe connected successfully
- `status` **REQ** : enum(string)  'connected'; 'not_configured' (provider is 'none'); 'auth_failed' (rejected — usually a wrong/expired API key); 'unreachable' (cal
- `latency_ms` opt : number|null  Round-trip latency of the probe call

### MapField-Input
_A field within a map-type entity label group. Supports recursion via type='map'._
- `type` opt : enum(string) default='text'  
- `description` opt : string default=''  
- `values` opt : array<LabelValue> default=[]  
- `fields` opt : map<string,MapField-Input> default={}  

### MapField-Output
_A field within a map-type entity label group. Supports recursion via type='map'._
- `type` opt : enum(string) default='text'  
- `description` opt : string default=''  
- `values` opt : array<LabelValue> default=[]  
- `fields` opt : map<string,MapField-Output> default={}  

### MemoriesTimeseriesResponse
_Time-series of memory ingestion bucketed by time and fact type._
- `bank_id` **REQ** : string  
- `period` **REQ** : string  One of: 1h, 12h, 1d, 7d, 30d, 90d.
- `trunc` **REQ** : string  Bucket granularity: minute, hour, day.
- `time_field` opt : string default='created_at'  Timestamp column used to assign each row to a bucket. `created_at` shows ingest time; `mentioned_at` / `occurred_start` show event
- `buckets` opt : array<MemoryTimeseriesBucket>  Per-bucket counts, always returned fully padded for the requested period.

### MemoryGraphEdge
_An edge between two memory units, in the Cytoscape ``{"data": {...}}`` envelope._
- `data` **REQ** : MemoryGraphEdgeData  

### MemoryGraphEdgeData
_The payload of one edge between two memory units._
- `id` **REQ** : string  Edge ID (``<source>-<target>-<linkType>``)
- `source` **REQ** : string  Source memory unit ID
- `target` **REQ** : string  Target memory unit ID
- `linkType` opt : string default=''  Link kind: 'entity', 'semantic', 'temporal', ...
- `weight` opt : number default=0.0  Link strength
- `entityName` opt : string default=''  Shared entity for an 'entity' link, empty otherwise
- `color` opt : string|null  Suggested edge colour for rendering
- `lineStyle` opt : string|null  Suggested edge line style for rendering

### MemoryGraphNode
_A memory-unit node, in the Cytoscape ``{"data": {...}}`` envelope the graph uses._
- `data` **REQ** : MemoryGraphNodeData  

### MemoryGraphNodeData
_The payload of one memory-unit node in the memory graph.

Extra keys are allowed and passed through, so typing this never drops a field the
server also returns._
- `id` **REQ** : string  Memory unit ID
- `label` opt : string default=''  Short display label (the text, truncated)
- `text` opt : string default=''  Full memory unit text
- `date` opt : string default=''  Event date (ISO 8601), empty when unknown
- `context` opt : string default=''  Context the memory was captured in
- `entities` opt : string default=''  Comma-separated entity names, 'None' when there are none
- `color` opt : string|null  Suggested node colour for rendering

### MemoryGraphTableRow
_One row of the flat table view that accompanies the memory graph._
- `id` **REQ** : string  Memory unit ID
- `text` opt : string default=''  Memory unit text
- `context` opt : string default=''  Context the memory was captured in ('N/A' when absent)
- `occurred_start` opt : string|null  Start of the event interval (ISO 8601)
- `occurred_end` opt : string|null  End of the event interval (ISO 8601)
- `mentioned_at` opt : string|null  When the memory was mentioned (ISO 8601)
- `date` opt : string|null  Deprecated: formatted event date, kept for backwards compatibility
- `entities` opt : string default=''  Comma-separated entity names, 'None' when there are none
- `document_id` opt : string|null  Source document ID
- `chunk_id` opt : string|null  Source chunk ID
- `fact_type` opt : string|null  Fact type: world, experience or observation
- `tags` opt : array<string> default=[]  Tags on this memory unit
- `created_at` opt : string|null  When the memory unit was created (ISO 8601)
- `proof_count` opt : integer|null  How many times the fact was independently seen

### MemoryItem
_Single memory item for retain._
- `content` **REQ** : string|array  The raw content to retain. Either a plain string, or an ordered list of content blocks so images sit inline where they actually ap
- `timestamp` opt : string|string|null  When the content occurred. Accepts an ISO 8601 datetime string (e.g. '2024-01-15T10:30:00Z'), null/omitted (defaults to now), or t
- `context` opt : string|null  
- `metadata` opt : object|null  
- `document_id` opt : string|null  Optional document ID for this memory item. Provide a distinct document_id per source document — items sharing a document_id are gr
- `entities` opt : array|null  Optional entities to combine with auto-extracted entities.
- `resolve_entities` opt : boolean default=True  Whether the names in 'entities' are resolved against the entities already in the bank. True (default) matches each name to a simil
- `tags` opt : array|null  Optional tags for visibility scoping. Memories with tags can be filtered during recall.
- `observation_scopes` opt : string|array|null  How to scope observations during consolidation. 'per_tag' runs one consolidation pass per individual tag, creating separate observ
- `strategy` opt : string|null  Named retain strategy for this item. Overrides the bank's default strategy for this item only. Strategies are defined in the bank 
- `update_mode` opt : string|null  How to handle an existing document with the same document_id. 'replace' (default) deletes old data and reprocesses from scratch. '

### MemoryTimeseriesBucket
_One bucket in the memory ingestion time-series._
- `time` **REQ** : string  Bucket start timestamp in ISO-8601 (UTC).
- `world` opt : integer default=0  World-fact memories ingested in this bucket.
- `experience` opt : integer default=0  Experience memories ingested in this bucket.
- `observation` opt : integer default=0  Observations recorded in this bucket.

### MemoryUnitListItem
_One row of the memory-unit listing.

Extra keys are allowed and passed through: the rows used to be an open object, and
typing them must not drop a field an older or newer server also returns._
- `id` **REQ** : string  Memory unit ID
- `text` opt : string default=''  The fact text
- `context` opt : string default=''  Context the memory was captured in
- `date` opt : string default=''  Event date (ISO 8601), empty when unknown
- `fact_type` opt : string|null  Fact type: world, experience or observation
- `document_id` opt : string|null  Source document ID
- `mentioned_at` opt : string|null  When the memory was mentioned (ISO 8601)
- `occurred_start` opt : string|null  Start of the event interval (ISO 8601)
- `occurred_end` opt : string|null  End of the event interval (ISO 8601)
- `entities` opt : string default=''  Comma-separated canonical entity names
- `chunk_id` opt : string|null  Source chunk ID
- `proof_count` opt : integer default=1  How many times the fact was independently seen
- `tags` opt : array<string> default=[]  Tags on this memory unit
- `metadata` opt : map<string,?> default={}  Arbitrary metadata stored with the memory
- `consolidated_at` opt : string|null  When consolidation last succeeded (ISO 8601)
- `consolidation_failed_at` opt : string|null  When consolidation last failed permanently (ISO 8601)
- `state` opt : string default='valid'  Curation state: 'valid' or 'invalidated'
- `invalidation_reason` opt : string|null  Why the fact was invalidated, if it was
- `invalidated_at` opt : string|null  When the fact was invalidated (ISO 8601)
- `edited_at` opt : string|null  When the fact was last edited by hand (ISO 8601)
- `updated_at` opt : string|null  Write watermark for this row (ISO 8601)
- `source_memory_ids` opt : array<string> default=[]  An observation's source facts; empty for a source fact

### MentalModelDeltaOperations
_Structured operations a delta refresh emitted against the existing document._
- `applied` opt : array<object>  Operations applied to the document, in order.
- `skipped` opt : array<object>  Operations dropped as invalid, each with a reason.

### MentalModelDryRunRefreshResult
_Preview of what a mental model refresh would do, having changed nothing.

Runs the real pipeline — same scope resolution, same reflect call, same
delta operations — then reports the result instead of _
- `mental_model_id` **REQ** : string  The mental model previewed.
- `name` **REQ** : string  Display name of the mental model.
- `requested_mode` **REQ** : enum(string)  The mode asked for (from the model's trigger, or overridden).
- `effective_mode` **REQ** : enum(string)  The mode the refresh actually ran in.
- `mode_fallback_reason` opt : string|null  Why delta was requested but not applied, if that happened.
- `outcome` **REQ** : enum(string)  What a real refresh would do with the document.
- `would_persist` **REQ** : boolean  Whether a real refresh would write new content.
- `scope` **REQ** : MentalModelRefreshScope  The resolved memory scope.
- `window` **REQ** : MentalModelRefreshWindow  The snapshot window read from.
- `facts` **REQ** : MentalModelFactCounts  Facts retrieved versus actually used.
- `based_on` opt : map<string,array>  The evidence this run would ground the document on, keyed by fact type — the same shape a refresh persists under reflect_response.
- `current_content` **REQ** : string  The model's content as it stands now.
- `candidate_content` **REQ** : string  Raw reflect synthesis, before any delta operations.
- `preview_content` **REQ** : string  The content a real refresh would store: the delta-edited document, or the candidate in full mode.
- `diff` **REQ** : string  Unified diff from current_content to preview_content. Empty when identical.
- `delta_operations` opt : MentalModelDeltaOperations|null  Structured operations emitted, in delta mode.
- `retraction` opt : MentalModelRetraction|null  Facts the document cites that no longer exist, and what a real refresh would do about them.
- `trace` **REQ** : MentalModelRefreshTrace  Execution trace of the run, always included for a dry run.
- `usage` opt : TokenUsage  Token usage across the run's LLM calls.
- `duration_ms` opt : integer default=0  Wall-clock duration of the run.
- `warnings` opt : array<string>  Conditions worth a human's attention, in plain language.

### MentalModelFactCounts
_Facts the refresh saw, keyed by fact type.

``retrieved`` and ``used`` diverging is the single most common cause of a
disappointing refresh: recall found plenty, but the reflect agent declared
none of_
- `retrieved` opt : map<string,integer>  Facts the reflect agent's tool calls returned, by fact type.
- `used` opt : map<string,integer>  Facts the agent declared it actually based the answer on, by fact type.

### MentalModelListResponse
_Response model for listing mental models._
- `items` **REQ** : array<MentalModelResponse>  
- `total` **REQ** : integer  Total number of mental models matching the filter (not just this page)
- `limit` **REQ** : integer  Page size that was applied
- `offset` **REQ** : integer  Offset that was applied

### MentalModelRefreshScope
_The memory scope a refresh actually resolved to.

A model's stored ``tags`` are not what filters memories — ``tags_match``
defaults to ``all_strict`` when tags are present, and ``tag_groups``
override_
- `tags` opt : array|null  Flat tags used to filter memories (null when unused).
- `tags_match` **REQ** : enum(string)  Resolved tag match mode.
- `tag_groups` opt : array|null  Compound tag expressions used instead of flat tags, when set.
- `fact_types` opt : array|null  Fact types retrieved (null means all).
- `exclude_mental_models` **REQ** : boolean  Whether other mental models were excluded from the reflect loop.
- `exclude_mental_model_ids` opt : array<string>  Mental models excluded by ID (always includes the model being refreshed).

### MentalModelRefreshTrace
_Execution trace of a mental model refresh, recorded when trigger.keep_trace is on.

Deliberately shaped like reflect's trace — the calls the agent made, plus the
refresh-specific decision — and nothin_
- `recorded_at` opt : string|null  When this trace was recorded.
- `effective_mode` **REQ** : enum(string)  Whether the refresh ran as full or delta.
- `mode_fallback_reason` opt : string|null  Why delta was requested but not applied, if that happened.
- `outcome` **REQ** : enum(string)  What the refresh did with the document.
- `tool_calls` opt : array<MentalModelTraceToolCall>  Reflect tool calls made during the refresh.
- `llm_calls` opt : array<LLMCallTrace>  LLM calls made during the refresh.
- `delta_operations` opt : MentalModelDeltaOperations|null  Structured operations emitted, in delta mode.
- `retraction` opt : MentalModelRetraction|null  Facts the document cited that no longer exist, when the refresh found any.
- `usage` opt : TokenUsage|null  Token usage across the refresh's LLM calls.
- `duration_ms` opt : integer default=0  Wall-clock duration of the refresh.
- `warnings` opt : array<string>  Conditions worth a human's attention, in plain language.

### MentalModelRefreshWindow
_The time window a refresh read memories from._
- `created_after` opt : string|null  Lower bound on when a memory last changed. Set only in delta mode, where it is the model's last_memory_seen_at — so a delta refres
- `created_before` **REQ** : string  Database-time snapshot bounding the refresh. Memories written or edited after this are not read, so they stay newer than the persi
- `watermark` opt : string|null  The last_memory_seen_at a real refresh would persist: the newest in-scope memory visible at the snapshot, not now(). Null means no

### MentalModelResponse
_Response model for a mental model (stored reflect response)._
- `id` **REQ** : string  
- `bank_id` **REQ** : string  
- `name` **REQ** : string  
- `source_query` opt : string|null  
- `content` opt : string|null  The mental model content as well-formatted markdown (auto-generated from reflect endpoint)
- `tags` opt : array<string> default=[]  
- `max_tokens` opt : integer|null  
- `trigger` opt : MentalModelTrigger-Output|null  
- `last_refreshed_at` opt : string|null  When a refresh last finished for this model — wall-clock, in ISO format. Advances on every refresh that completes, including one t
- `last_memory_seen_at` opt : string|null  How far through the bank's memories this model is written — the newest in-scope memory the last refresh saw, in ISO format. Stands
- `created_at` opt : string|null  
- `reflect_response` opt : object|null  Full reflect API response payload including based_on facts and observations
- `is_stale` opt : boolean|null  True when memories matching this mental model's tag/fact_type scope have been written since last_memory_seen_at — the same check t

### MentalModelRetraction
_Facts the document cited that no longer exist, and what was done about them.

A retraction is the one refresh input that is *invisible* in every other
report: the fact is gone from the bank, so it app_
- `fact_ids` opt : array<string>  Ids the document's based_on cites that no longer exist in the bank.
- `fact_texts` opt : array<string>  What those facts said, as the document recorded them. The rows themselves are gone — an observation swept away with its source kee
- `applied` **REQ** : boolean  Whether the pass that removes them ran to completion (zero edits still counts).
- `deferred_reason` opt : string|null  Why removal was postponed, when it was. Re-ingested facts return under new ids once consolidation catches up, so a retraction seen

### MentalModelTraceToolCall
_One reflect tool call made during a refresh.

``output`` is carried only by the dry run, which persists nothing. The trace
stored on the model row keeps ``result_count`` instead: it is re-read on ever_
- `tool` **REQ** : string  Tool name: recall, search_observations, get_mental_model, expand, …
- `reason` opt : string|null  The agent's stated reason for the call.
- `input` opt : map<string,?>  Tool input parameters.
- `output` opt : object|null  What the tool returned. Present on a dry run, which stores nothing; omitted from the trace persisted by a real refresh to keep tha
- `updated_at` opt : string|null  The refresh window's lower bound as given to this call — the delta watermark. Named for what it actually filters: the predicate is
- `result_count` opt : integer|null  Number of items the tool returned, when countable.
- `duration_ms` **REQ** : integer  Execution time in milliseconds.
- `iteration` opt : integer default=0  Agent loop iteration (1-based) this call belongs to.

### MentalModelTrigger-Input
_Trigger settings for a mental model.

Inherits the reflect options an operator can also default per bank
(``reflect_default_options``): set here they apply to this model's refreshes
only, and win over_
- `reflect_search_observations_max_tokens` opt : integer|null  Token budget for reflect's search_observations tool when the model names none. Observation evidence is often the largest contribut
- `reflect_search_observations_include_entities` opt : boolean|null  Whether search_observations attaches resolved entity names to each observation. Entities can be more than half the serialized tool
- `mode` opt : enum(string) default='full'  Refresh mode. 'full' (default) regenerates the mental model content from scratch on each refresh. 'delta' performs surgical edits 
- `refresh_after_consolidation` opt : boolean default=False  If true, refresh this mental model after observations consolidation (real-time mode)
- `refresh_cron` opt : string|null  Cron expression (UTC, standard 5-field syntax, e.g. '0 3 * * *' for daily at 03:00 UTC) for refreshing this mental model on a fixe
- `min_refresh_interval_seconds` opt : integer|null  Minimum seconds between two automatic refreshes of this mental model. A triggered refresh that arrives sooner is not dropped: it i
- `fact_types` opt : array|null  Filter which fact types are retrieved during reflect. None means all types (world, experience, observation).
- `exclude_mental_models` opt : boolean default=False  If true, exclude all mental models from the reflect loop (skip search_mental_models tool).
- `exclude_mental_model_ids` opt : array|null  Exclude specific mental models by ID from the reflect loop.
- `tags_match` opt : string|null  Override how the model's tags filter memories during refresh. If not set, defaults to 'all_strict' when the model has tags (securi
- `tag_groups` opt : array|null  Compound boolean tag expressions to use during refresh instead of the model's own tags. When set, these tag groups are passed to r
- `include_chunks` opt : boolean|null  Override whether the internal recall used during refresh returns raw chunk text. None means use the bank/global config default (re
- `recall_max_tokens` opt : integer|null  Override the token budget for facts returned by the internal recall during refresh. None means use the bank/global config default 
- `recall_chunks_max_tokens` opt : integer|null  Override the token budget for raw chunks returned by the internal recall during refresh. None means use the bank/global config def
- `response_schema` opt : object|null  Optional JSON Schema for structured output. When set, each refresh runs the same structured-output extraction as reflect's respons
- `keep_trace` opt : boolean default=False  If true, every refresh of this mental model records how it reached its result under reflect_response.trace: the mode it ran in and

### MentalModelTrigger-Output
_Trigger settings for a mental model.

Inherits the reflect options an operator can also default per bank
(``reflect_default_options``): set here they apply to this model's refreshes
only, and win over_
- `reflect_search_observations_max_tokens` opt : integer|null  Token budget for reflect's search_observations tool when the model names none. Observation evidence is often the largest contribut
- `reflect_search_observations_include_entities` opt : boolean|null  Whether search_observations attaches resolved entity names to each observation. Entities can be more than half the serialized tool
- `mode` opt : enum(string) default='full'  Refresh mode. 'full' (default) regenerates the mental model content from scratch on each refresh. 'delta' performs surgical edits 
- `refresh_after_consolidation` opt : boolean default=False  If true, refresh this mental model after observations consolidation (real-time mode)
- `refresh_cron` opt : string|null  Cron expression (UTC, standard 5-field syntax, e.g. '0 3 * * *' for daily at 03:00 UTC) for refreshing this mental model on a fixe
- `min_refresh_interval_seconds` opt : integer|null  Minimum seconds between two automatic refreshes of this mental model. A triggered refresh that arrives sooner is not dropped: it i
- `fact_types` opt : array|null  Filter which fact types are retrieved during reflect. None means all types (world, experience, observation).
- `exclude_mental_models` opt : boolean default=False  If true, exclude all mental models from the reflect loop (skip search_mental_models tool).
- `exclude_mental_model_ids` opt : array|null  Exclude specific mental models by ID from the reflect loop.
- `tags_match` opt : string|null  Override how the model's tags filter memories during refresh. If not set, defaults to 'all_strict' when the model has tags (securi
- `tag_groups` opt : array|null  Compound boolean tag expressions to use during refresh instead of the model's own tags. When set, these tag groups are passed to r
- `include_chunks` opt : boolean|null  Override whether the internal recall used during refresh returns raw chunk text. None means use the bank/global config default (re
- `recall_max_tokens` opt : integer|null  Override the token budget for facts returned by the internal recall during refresh. None means use the bank/global config default 
- `recall_chunks_max_tokens` opt : integer|null  Override the token budget for raw chunks returned by the internal recall during refresh. None means use the bank/global config def
- `response_schema` opt : object|null  Optional JSON Schema for structured output. When set, each refresh runs the same structured-output extraction as reflect's respons
- `keep_trace` opt : boolean default=False  If true, every refresh of this mental model records how it reached its result under reflect_response.trace: the mode it ran in and

### MinScores
_Optional per-stage score floors for recall. Every floor is inclusive (``>=``).

The four floors act at two different levels, and the distinction decides what a
returned result is guaranteed to satisfy_
- `semantic` opt : number|null  Retrieval-level, semantic arm only: minimum vector similarity (0-1). A result the semantic arm did not surface reports `semantic: 
- `keyword` opt : number|null  Retrieval-level, keyword arm only: minimum keyword/full-text (BM25) score. A result the keyword arm did not surface reports `keywo
- `reranker` opt : number|null  Post-query: minimum normalized reranker score (0-1). Applied to every returned result.
- `final` opt : number|null  Post-query: minimum final ranking score. Applied to every returned result.

### ObservationScope
_A distinct observation scope: an exact tag set plus its observation count._
- `tags` **REQ** : array<string>  The exact tag set defining this scope (normalized order). Empty list is the global/untagged scope.
- `count` **REQ** : integer  Number of observations that live under this scope

### ObservationScopesResponse
_Response model for the observation scopes enumeration endpoint._
- `scopes` **REQ** : array<ObservationScope>  Distinct observation scopes, most populous first
- `total` **REQ** : integer  Total number of distinct scopes in the bank (ignores limit/offset)
- `limit` **REQ** : integer  Maximum number of scopes returned in this page
- `offset` **REQ** : integer  Offset this page started at

### OperationProgress
_Last-known progress snapshot for a long-running async operation.

Written at coarse phase/batch boundaries by the worker (consolidation, batch
retain). Lets an operator polling the operation status AP_
- `stage` **REQ** : string  Coarse phase the operation last reported (e.g. 'processing_batch').
- `at` **REQ** : string  ISO-8601 timestamp when this snapshot was written.
- `processed` opt : integer|null  Units of work finished so far (sub-batches, memories), when known.
- `total` opt : integer|null  Total units of work for the operation, when known.
- `detail` opt : object|null  Operation-specific counters (e.g. observations_created, round, items_in_sub_batch).

### OperationResponse
_Response model for a single async operation._
- `id` **REQ** : string  
- `task_type` **REQ** : string  
- `items_count` **REQ** : integer  
- `document_id` opt : string|null  
- `filename` opt : string|null  Original filename for file-conversion operations (file_convert_retain); null for other task types.
- `mental_model_id` opt : string|null  Mental model this operation acted on (refresh_mental_model); null for other task types. Without it the list cannot say which model
- `details` opt : RefreshMentalModelOperationDetails|null  Typed, per-operation-type outcome detail, discriminated by its own `operation_type`. Populated for `refresh_mental_model` operatio
- `created_at` **REQ** : string  
- `updated_at` opt : string|null  When this operation's row last changed (claim, progress heartbeat, or completion).
- `status` **REQ** : string  
- `error_message` **REQ** : string|null  
- `retry_count` opt : integer|null  Number of times this operation has been retried after failure.
- `next_retry_at` opt : string|null  When the worker will next attempt this operation. For a pending operation, a value in the future indicates the task is waiting rat
- `progress` opt : OperationProgress|null  Last-known progress snapshot for a running operation; null if none was recorded.

### OperationStatusResponse
_Response model for getting a single operation status._
- `operation_id` **REQ** : string  
- `status` **REQ** : enum(string)  
- `operation_type` opt : string|null  
- `created_at` opt : string|null  
- `updated_at` opt : string|null  
- `completed_at` opt : string|null  
- `error_message` opt : string|null  
- `retry_count` opt : integer|null  Number of times this operation has been retried after failure.
- `next_retry_at` opt : string|null  When the worker will next attempt this operation. For a pending operation, a value in the future indicates the task is parked — a 
- `progress` opt : OperationProgress|null  Last-known progress snapshot for a running operation; null if none was recorded.
- `result_metadata` opt : object|null  Internal metadata for debugging. Structure may change without notice. Not for production use.
- `details` opt : RefreshMentalModelOperationDetails|null  Typed, per-operation-type outcome detail, discriminated by its own `operation_type`. Populated for `refresh_mental_model` operatio
- `child_operations` opt : array|null  Child operations for batch operations (if applicable)
- `task_payload` opt : object|null  Raw task payload (params the operation was submitted with). Only populated when include_payload=true.

### OperationsListResponse
_Response model for list operations endpoint._
- `bank_id` **REQ** : string  
- `total` **REQ** : integer  
- `limit` **REQ** : integer  
- `offset` **REQ** : integer  
- `operations` **REQ** : array<OperationResponse>  

### PromptBlockModel
_One block of a message: its text, and the setting that decides it.

The **active** blocks of a message concatenate back to the exact text sent, so a
client can render them separately without showing t_
- `text` **REQ** : string  The block's text; empty when the block is inactive.
- `source` **REQ** : enum(string)  `config` — produced by a setting (`field` names it); `builtin` — Hindsight's own wording.
- `field` opt : string default=''  Config field behind this block; empty when no single field owns it.
- `section` opt : string default=''  Slug for a part the preview names itself and no field owns: `bank_identity`, `disposition`, `directives`. Empty otherwise.
- `heading` opt : string default=''  The section heading the prompt text carries at this point, extracted from the prompt itself. Empty when it carries none.
- `active` opt : boolean default=True  Whether this block is in the prompt as configured.
- `value` opt : string|null  The field's effective value; null when unset.
- `kind` **REQ** : enum(string)  Shape of the value, so a client can offer the right control for editing it.
- `choices` opt : array|null  Allowed values, when `kind` is `choice`.
- `editable` opt : boolean default=False  Whether this bank may override the field via the bank config API. Server-level fields shape the prompt but cannot be set per bank,

### PromptMessageModel
_One message of the request, as the blocks it is built from._
- `role` **REQ** : enum(string)  
- `blocks` opt : array<PromptBlockModel>  

### PromptPreviewRequest
_Request to render the prompts an operation would send, without calling an LLM.

The operation is the whole request: everything that shapes the prompt comes from
the bank — its resolved config, profile_
- `operation` opt : enum(string) default='retain'  Which operation's prompts to render.
- `strategy` opt : string|null  Name of a retain strategy to render under (a key of the bank's `retain_strategies`). Retain only. Omit it and the bank's `retain_d

### PromptPreviewResponse
_The messages one call of the requested operation would send.

`messages` is in send order, system first. Both are always present because a
mission is not necessarily in the system prompt: retain and c_
- `messages` opt : array<PromptMessageModel>  Request messages, in send order. Each is given as the blocks it is built from.
- `strategy` opt : string|null  The retain strategy these prompts were rendered under, if any.
- `strategies` opt : array<string>  Names of the bank's retain strategies, so a client can offer them without a second call.
- `run_settings` opt : array<RunSettingModel>  Settings that shape the operation without appearing in its prompt, such as chunk sizes.
- `response_schema` opt : object|null  JSON schema the response is constrained to, when the operation constrains it.
- `skipped_reason` opt : string|null  Why no prompt is sent, when the configuration means none is.

### RecallRequest
_Request model for recall endpoint._
- `query` **REQ** : string  
- `types` opt : array|null  List of fact types to recall: 'world', 'experience', 'observation'. Defaults to all fact types if not specified.
- `prefer_observations` opt : boolean default=False  When recalling raw facts ('world'/'experience') together with 'observation', drop any raw fact that an observation in the results 
- `budget` opt : Budget default='mid'  
- `max_tokens` opt : integer default=4096  
- `trace` opt : boolean default=False  
- `query_timestamp` opt : string|null  ISO format date string (e.g., '2023-05-30T23:40:00'). Used as the query-time anchor for relative temporal expressions and recency 
- `include` opt : IncludeOptions default={}  Options for including additional data (entities are included by default)
- `tags` opt : array|null  Filter memories by tags. If not specified, all memories are returned. Omitting tags (or passing []) together with tags_match='exac
- `tags_match` opt : enum(string) default='any'  How to match tags: 'any' (OR, includes untagged), 'all' (AND, includes untagged), 'any_strict' (OR, excludes untagged), 'all_stric
- `tag_groups` opt : array|null  Compound tag filter using boolean groups. Groups in the list are AND-ed. Each group is a leaf {tags, match} or compound {and: [...
- `min_scores` opt : MinScores|null  Optional per-stage score floors, each inclusive (`>=`). `semantic` and `keyword` are retrieval-level cutoffs pushed into the SQL a
- `temporal_window` opt : TemporalWindow|null  Window for the temporal retrieval arm, supplied instead of extracting dates from `query`. Set this when you already know the range

### RecallResponse
_Response model for recall endpoints._
- `results` **REQ** : array<RecallResult>  
- `trace` opt : object|null  
- `entities` opt : object|null  Entity states for entities mentioned in results
- `chunks` opt : object|null  Chunks for facts, keyed by chunk_id
- `source_facts` opt : object|null  Source facts for observation-type results, keyed by fact ID
- `source_facts_truncated` opt : boolean|null  Whether the source_facts map was cut short by the token budget. When true, some IDs in results[].source_fact_ids have no entry in 

### RecallResult
_Single recall result item._
- `id` **REQ** : string  
- `text` **REQ** : string  
- `type` opt : string|null  
- `entities` opt : array|null  
- `context` opt : string|null  
- `occurred_start` opt : string|null  
- `occurred_end` opt : string|null  
- `mentioned_at` opt : string|null  
- `document_id` opt : string|null  
- `metadata` opt : object|null  
- `chunk_id` opt : string|null  
- `tags` opt : array|null  
- `source_fact_ids` opt : array|null  
- `scores` opt : RecallScores|null  
- `attachments` opt : array|null  Attachments this fact was drawn from, as recorded per fact at extraction time — the same edge the memory read endpoints return, no

### RecallScores
_Per-result recall scores from different stages of the pipeline.

``final`` is the value results are ranked by. The others are diagnostic and
can be filtered on via the recall ``min_scores`` request pa_
- `final` **REQ** : number  Final ranking score (combined reranker + recency/temporal/proof boosts)
- `reranker` opt : number|null  Cross-encoder relevance, normalized 0-1. None when the reranker is a passthrough (rrf/interleave modes).
- `semantic` opt : number|null  Vector cosine similarity (0-1). None if this result was not surfaced semantically.
- `keyword` opt : number|null  Keyword/full-text (BM25) score (>= 0, unbounded). None if this result was not surfaced by keyword search.

### RecoverConsolidationResponse
_Response model for recovering failed consolidation._
- `retried_count` **REQ** : integer  

### ReflectBasedOn
_Evidence the response is based on: memories, mental models, and directives._
- `memories` opt : array<ReflectFact> default=[]  Memory facts used to generate the response
- `mental_models` opt : array<ReflectMentalModel> default=[]  Mental models used during reflection
- `directives` opt : array<ReflectDirective> default=[]  Directives applied during reflection

### ReflectDirective
_A directive applied during reflect._
- `id` **REQ** : string  Directive ID
- `name` **REQ** : string  Directive name
- `content` **REQ** : string  Directive content

### ReflectFact
_A fact used in think response._
- `id` opt : string|null  
- `text` **REQ** : string  Fact text. When type='observation', this contains markdown-formatted consolidated knowledge
- `type` opt : string|null  
- `context` opt : string|null  
- `occurred_start` opt : string|null  
- `occurred_end` opt : string|null  

### ReflectIncludeOptions
_Options for including additional data in reflect results._
- `facts` opt : FactsIncludeOptions|null  Include facts that the answer is based on. Set to {} to enable, null to disable (default: disabled).
- `tool_calls` opt : ToolCallsIncludeOptions|null  Include tool calls trace. Set to {} for full trace (input+output), {output: false} for inputs only.

### ReflectLLMCall
_An LLM call made during reflect agent execution._
- `scope` **REQ** : string  Call scope: agent_1, agent_2, final, etc.
- `duration_ms` **REQ** : integer  Execution time in milliseconds

### ReflectMentalModel
_A mental model used during reflect._
- `id` **REQ** : string  Mental model ID
- `text` **REQ** : string  Mental model content
- `context` opt : string|null  Additional context

### ReflectRequest
_Request model for reflect endpoint._
- `reflect_search_observations_max_tokens` opt : integer|null  Token budget for reflect's search_observations tool when the model names none. Observation evidence is often the largest contribut
- `reflect_search_observations_include_entities` opt : boolean|null  Whether search_observations attaches resolved entity names to each observation. Entities can be more than half the serialized tool
- `query` **REQ** : string  
- `budget` opt : Budget default='low'  
- `context` opt : string|null  DEPRECATED: Additional context is now concatenated with the query. Pass context directly in the query field instead. If provided, 
- `max_tokens` opt : integer default=4096  Maximum tokens for the response
- `include` opt : ReflectIncludeOptions  Options for including additional data (disabled by default)
- `response_schema` opt : object|null  Optional JSON Schema for structured output. When provided, the response will include a 'structured_output' field with the LLM resp
- `tags` opt : array|null  Scope raw facts, observations, mental models, and tagged directives during reflection. With no tags, memory retrieval is unfiltere
- `tags_match` opt : enum(string) default='any'  How to match tags: 'any' (OR, includes untagged), 'all' (AND, includes untagged), 'any_strict' (OR, excludes untagged), 'all_stric
- `tag_groups` opt : array|null  Compound tag filter using boolean groups. Groups in the list are AND-ed. Each group is a leaf {tags, match} or compound {and: [...
- `apply_all_directives` opt : boolean default=False  Apply every active directive regardless of tags. By default directives are scoped like memories: untagged directives always apply,
- `fact_types` opt : array|null  Filter which fact types are retrieved during reflect. None means all types (world, experience, observation).
- `exclude_mental_models` opt : boolean default=False  If true, exclude all mental models from the reflect loop (skip search_mental_models tool).
- `exclude_mental_model_ids` opt : array|null  Exclude specific mental models by ID from the reflect loop.

### ReflectResponse
_Response model for think endpoint._
- `text` **REQ** : string  The reflect response as well-formatted markdown (headers, lists, bold/italic, code blocks, etc.)
- `based_on` opt : ReflectBasedOn|null  Evidence used to generate the response. Only present when include.facts is set.
- `structured_output` opt : object|null  Structured output parsed according to the request's response_schema. Only present when response_schema was provided in the request
- `structured_output_error` opt : string|null  Why structured output could not be produced. Present only when a response_schema was given and the extraction call failed (provide
- `usage` opt : TokenUsage|null  Token usage metrics for LLM calls during reflection.
- `trace` opt : ReflectTrace|null  Execution trace of tool and LLM calls. Only present when include.tool_calls is set.

### ReflectToolCall
_A tool call made during reflect agent execution._
- `tool` **REQ** : string  Tool name: lookup, recall, learn, expand
- `input` **REQ** : map<string,?>  Tool input parameters
- `output` opt : object|null  Tool output (only included when include.tool_calls.output is true)
- `duration_ms` **REQ** : integer  Execution time in milliseconds
- `iteration` opt : integer default=0  Iteration number (1-based) when this tool was called

### ReflectTrace
_Execution trace of LLM and tool calls during reflection._
- `tool_calls` opt : array<ReflectToolCall> default=[]  Tool calls made during reflection
- `llm_calls` opt : array<ReflectLLMCall> default=[]  LLM calls made during reflection

### RefreshMentalModelOperationDetails
_What a refresh_mental_model operation did, on the operation record itself.

Reported under an async operation's ``details``, which is keyed by
``operation_type``: each type that has a typed outcome to_
- `operation_type` opt : string default='refresh_mental_model'  Discriminator: which operation type this detail describes.
- `outcome` **REQ** : enum(string)  What the refresh did with the document: rewrote it (`content_written`), produced a document identical to the stored one (`content_
- `failure_reason` opt : string|null  Why the refresh refused to write, finer-grained than the outcome — it distinguishes an op call that failed from one whose operatio

### ReprocessDocumentResponse
_Response model for reprocess document endpoint._
- `success` **REQ** : boolean  
- `operation_id` **REQ** : string  
- `items_count` **REQ** : integer  

### RetainRequest
_Request model for retain endpoint._
- `items` **REQ** : array<MemoryItem>  
- `async` opt : boolean default=False  If true, process asynchronously in background. If false, wait for completion (default: false)
- `document_tags` opt : array|null  Deprecated. Use item-level tags instead.
- `operation_id` opt : string|null  Optional client-supplied UUID used as the identity of an async retain operation. Re-submitting with the same operation_id returns 

### RetainResponse
_Response model for retain endpoint._
- `success` **REQ** : boolean  
- `bank_id` **REQ** : string  
- `items_count` **REQ** : integer  
- `async` **REQ** : boolean  Whether the operation was processed asynchronously
- `operation_id` opt : string|null  Operation ID for tracking async operations. Use GET /v1/default/banks/{bank_id}/operations to list operations. Only present when a
- `operation_ids` opt : array|null  Operation IDs when items were submitted as multiple strategy groups (async=true with mixed per-item strategies). operation_id is s
- `usage` opt : TokenUsage|null  Token usage metrics for LLM calls during fact extraction (only present for synchronous operations)

### RetryOperationResponse
_Response model for retry operation endpoint._
- `success` **REQ** : boolean  
- `message` **REQ** : string  
- `operation_id` **REQ** : string  

### RunSettingModel
_A setting that shapes the operation without appearing in its prompt.

Chunk sizes decide how the input is cut before extraction runs, so they change
what comes back while contributing no prompt text —_
- `field` **REQ** : string  
- `value` opt : string|null  Effective value; null when unset.
- `kind` **REQ** : enum(string)  Shape of the value, so a client can offer the right control.
- `editable` opt : boolean default=False  Whether this bank may override the field via the bank config API.

### SourceFactsIncludeOptions
_Options for including source facts for observation-type results._
- `max_tokens` opt : integer default=4096  Maximum total tokens for source facts across all observations (-1 = unlimited)
- `max_tokens_per_observation` opt : integer default=-1  Maximum tokens of source facts per observation (-1 = unlimited)

### TagGroupAnd-Input
_Compound AND group: all child filters must match._
- `and` **REQ** : array<?>  

### TagGroupAnd-Output
_Compound AND group: all child filters must match._
- `and` **REQ** : array<?>  

### TagGroupLeaf
_A leaf tag filter: matches memories by tag list and match mode._
- `tags` **REQ** : array<string>  
- `match` opt : enum(string) default='any_strict'  
- `resolve` opt : enum(string) default='exact'  

### TagGroupNot-Input
_Compound NOT group: child filter must NOT match._
- `not` **REQ** : TagGroupLeaf|TagGroupAnd-Input|TagGroupOr-Input|TagGroupNot-Input  

### TagGroupNot-Output
_Compound NOT group: child filter must NOT match._
- `not` **REQ** : TagGroupLeaf|TagGroupAnd-Output|TagGroupOr-Output|TagGroupNot-Output  

### TagGroupOr-Input
_Compound OR group: at least one child filter must match._
- `or` **REQ** : array<?>  

### TagGroupOr-Output
_Compound OR group: at least one child filter must match._
- `or` **REQ** : array<?>  

### TagItem
_Single tag with usage count._
- `tag` **REQ** : string  The tag value
- `count` **REQ** : integer  Number of memories with this tag

### TemporalWindow
_A caller-supplied window for recall's temporal arm.

Supplying this **replaces the date extraction** recall would otherwise run
over the query text, so a caller that already knows the range it means d_
- `start` **REQ** : string  Start of the window (inclusive).
- `end` **REQ** : string  End of the window (inclusive).

### TextContentBlock
_A run of text within a multimodal item, in the position the caller wrote it._
- `type` **REQ** : string  
- `text` **REQ** : string  

### TokenUsage
_Token usage metrics for LLM calls.

Tracks input/output tokens for a single request to enable
per-request cost tracking and monitoring._
- `input_tokens` opt : integer default=0  Number of input/prompt tokens consumed
- `output_tokens` opt : integer default=0  Number of visible output/completion tokens generated (excludes reasoning/thoughts)
- `total_tokens` opt : integer default=0  Total tokens (input + output, excludes thoughts)
- `cached_tokens` opt : integer default=0  Cached/cache-read prompt tokens, when reported by the provider
- `thoughts_tokens` opt : integer default=0  Reasoning/thinking tokens generated by the model. Billed at the output rate by some providers (e.g. Gemini 2.5+ family) but not su

### ToolCallsIncludeOptions
_Options for including tool calls in reflect results._
- `output` opt : boolean default=True  Include tool outputs in the trace. Set to false to only include inputs (smaller payload).

### UpdateDirectiveRequest
_Request model for updating a directive._
- `name` opt : string|null  New name
- `content` opt : string|null  New content
- `priority` opt : integer|null  New priority
- `is_active` opt : boolean|null  New active status
- `tags` opt : array|null  New tags

### UpdateDispositionRequest
_Request model for updating disposition traits._
- `disposition` **REQ** : DispositionTraits  

### UpdateDocumentRequest
_Request model for updating a document's mutable fields._
- `tags` opt : array|null  The complete new set of tags for the document and its memory units — this REPLACES the existing tags rather than adding to them, s

### UpdateDocumentResponse
_Response model for update document endpoint._
- `success` opt : boolean default=True  

### UpdateMemoryRequest
_Request model for curating a single memory unit (edit / invalidate / revert).

Provide ``text`` to correct the fact, and/or ``state`` to invalidate
('invalidated') or revert ('valid') it. ``reason`` i_
- `text` opt : string|null  New fact text. Re-embeds the memory, drops its derived observations and links, and triggers re-consolidation.
- `context` opt : string|null  New context for the fact. '' clears it; omit to leave unchanged.
- `occurred_start` opt : string|null  New occurred-range start (ISO 8601). '' clears it; omit to leave unchanged.
- `occurred_end` opt : string|null  New occurred-range end (ISO 8601). '' clears it; omit to leave unchanged.
- `fact_type` opt : string|null  Reclassify the fact: 'world' or 'experience'. Omit to leave unchanged.
- `entities` opt : array|null  Replace the fact's entities. How each name is matched to an entity is governed by 'resolve_entities'. '[]' detaches all entities. 
- `resolve_entities` opt : boolean default=True  Whether the names in 'entities' are resolved against the entities already in the bank. True (default) is what retain does: a simil
- `state` opt : string|null  Curation state: 'invalidated' to soft-retire the memory (excluded from recall/consolidation, links and derived observations pruned
- `reason` opt : string|null  Optional free-text reason recorded when invalidating.

### UpdateMentalModelRequest
_Request model for updating a mental model._
- `name` opt : string|null  New name for the mental model
- `source_query` opt : string|null  New source query for the mental model
- `max_tokens` opt : integer|null  Maximum tokens for generated content
- `tags` opt : array|null  Tags for scoped visibility
- `trigger` opt : MentalModelTrigger-Input|null  Trigger settings

### UpdateNodeRequest
_Rename/move a node and/or update a page's options. Each field applies only
when present in the request body._
- `name` opt : string|null  
- `parent_id` opt : string|null  
- `source_query` opt : string|null  
- `tags` opt : array|null  Replaces the page's tags, which SCOPE which memories it is built from. Pass `[]` to clear them and rebuild the page from the whole
- `max_tokens` opt : integer|null  
- `trigger` opt : MentalModelTrigger-Input|null  Refresh settings to change. Applied as a patch: only the fields present in this object are updated, and the rest keep the page's c

### UpdateWebhookRequest
_Request model for updating a webhook. Only provided fields are updated._
- `url` opt : string|null  HTTP(S) endpoint URL
- `secret` opt : string|null  HMAC-SHA256 signing secret. Omit to keep existing; send null to clear.
- `event_types` opt : array|null  List of event types
- `enabled` opt : boolean|null  Whether this webhook is active
- `http_config` opt : WebhookHttpConfig|null  HTTP delivery configuration

### ValidationError
- `loc` **REQ** : array<?>  
- `msg` **REQ** : string  
- `type` **REQ** : string  
- `input` opt : ?  
- `ctx` opt : object  

### VersionResponse
_Response model for the version/info endpoint._
- `api_version` **REQ** : string  API version string
- `features` **REQ** : FeaturesInfo  Enabled feature flags

### WebhookDeliveryListResponse
_Response model for listing webhook deliveries._
- `items` **REQ** : array<WebhookDeliveryResponse>  
- `next_cursor` opt : string|null  

### WebhookDeliveryResponse
_Response model for a webhook delivery record._
- `id` **REQ** : string  
- `webhook_id` **REQ** : string|null  
- `url` **REQ** : string  
- `event_type` **REQ** : string  
- `status` **REQ** : string  
- `attempts` **REQ** : integer  
- `next_retry_at` opt : string|null  
- `last_error` opt : string|null  
- `last_response_status` opt : integer|null  
- `last_response_body` opt : string|null  
- `last_attempt_at` opt : string|null  
- `created_at` opt : string|null  
- `updated_at` opt : string|null  

### WebhookHttpConfig
_HTTP delivery configuration for a webhook._
- `method` opt : string default='POST'  HTTP method: GET or POST
- `timeout_seconds` opt : integer default=30  HTTP request timeout in seconds
- `headers` opt : map<string,string>  Custom HTTP headers
- `params` opt : map<string,string>  Custom HTTP query parameters

### WebhookListResponse
_Response model for listing webhooks._
- `items` **REQ** : array<WebhookResponse>  
- `total` **REQ** : integer  Total number of webhooks on the bank (ignores limit/offset)
- `limit` **REQ** : integer  Maximum number of webhooks returned in this page
- `offset` **REQ** : integer  Offset this page started at

### WebhookResponse
_Response model for a webhook._
- `id` **REQ** : string  
- `bank_id` **REQ** : string|null  
- `url` **REQ** : string  
- `secret` opt : string|null  Signing secret (redacted in responses)
- `event_types` **REQ** : array<string>  
- `enabled` **REQ** : boolean  
- `http_config` opt : WebhookHttpConfig  
- `created_at` opt : string|null  
- `updated_at` opt : string|null  
