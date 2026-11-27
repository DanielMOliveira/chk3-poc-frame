-- 0002 (27/11): parcelas máximas, contrato JSON com campos opcionais, token do frame em tabela quente
-- + histórico (avaliação CHK-5, 26/11) e entrega do webhook pelo Worker. Só ALTER/CREATE: preserva os dados.

-- Parcelas: o número informado na criação é o MÁXIMO. Sem ele vale o máximo do cadastro (padrão 12).
ALTER TABLE channels ALTER COLUMN max_installments SET DEFAULT 12;
UPDATE channels SET max_installments = 12 WHERE id = 'canal-fake';
-- Webhook por canal. NULL = usa WEBHOOK_DEFAULT_URL do .env do Worker.
ALTER TABLE channels ADD COLUMN webhook_url text NULL CHECK (webhook_url IS NULL OR webhook_url LIKE 'https://%');

-- Contrato JSON (ata 26/11): só o valor é obrigatório. Dados opcionais do pagamento (nunca vão para log).
ALTER TABLE payment_sessions
    ADD COLUMN billing          jsonb NULL,
    ADD COLUMN url_return       text  NULL,
    ADD COLUMN soft_descriptor  text  NULL;

-- Token do frame (quente e estreita): só o hash (SHA-256 de 32 bytes CSPRNG, nunca o transactionId),
-- a sessão, o snapshot mínimo do cadastro, o TTL e o uso único. O Worker expurga em lotes pequenos.
ALTER TABLE credentials RENAME TO frame_tokens;
ALTER INDEX ix_credentials_tx RENAME TO ix_frame_tokens_tx;
ALTER TABLE frame_tokens
    ADD COLUMN brands            text[]      NULL,
    ADD COLUMN max_installments  integer     NULL,
    ADD COLUMN used_at           timestamptz NULL;   -- uso único: marcado no passo 10c para todos os tokens da sessão
UPDATE frame_tokens t SET brands = p.brands, max_installments = p.max_installments
    FROM payment_sessions p WHERE p.transaction_id = t.transaction_id;
UPDATE frame_tokens t SET used_at = a.created_at FROM attempts a WHERE a.transaction_id = t.transaction_id;
ALTER TABLE frame_tokens ALTER COLUMN brands SET NOT NULL, ALTER COLUMN max_installments SET NOT NULL;
CREATE INDEX ix_frame_tokens_expires ON frame_tokens (expires_at);
CREATE INDEX ix_frame_tokens_used ON frame_tokens (used_at) WHERE used_at IS NOT NULL;

-- Histórico do token: só metadados sanitizados. Nunca o token nem o hash.
CREATE TABLE frame_token_history (
    id              bigserial PRIMARY KEY,
    transaction_id  uuid NOT NULL,
    kind            text NOT NULL,
    created_at      timestamptz NOT NULL,
    expires_at      timestamptz NOT NULL,
    used_at         timestamptz NULL,
    motivo          text NOT NULL CHECK (motivo IN ('expirado', 'usado')),
    purged_at       timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_frame_token_history_tx ON frame_token_history (transaction_id);

-- Outbox: entrega do webhook (passo 16) com retentativa.
ALTER TABLE outbox
    ADD COLUMN event_id         uuid        NOT NULL DEFAULT gen_random_uuid(),
    ADD COLUMN attempts         integer     NOT NULL DEFAULT 0,
    ADD COLUMN next_attempt_at  timestamptz NOT NULL DEFAULT now(),
    ADD COLUMN last_status      integer     NULL,
    ADD COLUMN last_error       text        NULL;
-- Eventos de antes do Worker não são enviados (evita disparar dezenas de webhooks antigos).
UPDATE outbox SET next_attempt_at = 'infinity', last_error = 'anterior_ao_worker_nao_enviado' WHERE delivered_at IS NULL;
CREATE INDEX ix_outbox_pending ON outbox (next_attempt_at) WHERE delivered_at IS NULL;
