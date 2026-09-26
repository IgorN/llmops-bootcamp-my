-- Базова схема. Студент розширює під власні потреби.

CREATE TABLE IF NOT EXISTS requests (
    request_id      UUID PRIMARY KEY,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    model           TEXT NOT NULL,
    provider        TEXT,
    prompt_version  TEXT,
    latency_ms      INTEGER,
    prompt_tokens   INTEGER,
    completion_tokens INTEGER,
    cost_usd        NUMERIC(10, 6),
    status          TEXT
);

CREATE TABLE IF NOT EXISTS prompts (
    name        TEXT NOT NULL,
    version     TEXT NOT NULL,
    body        TEXT NOT NULL,
    active      BOOLEAN NOT NULL DEFAULT false,
    created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (name, version)
);

-- Рівно одна активна версія на промпт. Тримає схема, а не код.
-- Частковий унікальний індекс тут не підійде, бо Postgres перевіряє його після
-- кожного рядка і наш UPDATE впав би з duplicate key на середині перемикання.
-- Відкладене обмеження перевіряється в кінці транзакції, коли стан уже коректний.
ALTER TABLE prompts DROP CONSTRAINT IF EXISTS one_active;
ALTER TABLE prompts ADD CONSTRAINT one_active
    EXCLUDE (name WITH =) WHERE (active) DEFERRABLE INITIALLY DEFERRED;

-- Журнал активацій. Хто, коли і яку версію зробив активною.
-- Пишеться в одній транзакції з UPDATE prompts, щоб стан і історія не розходились.
CREATE TABLE IF NOT EXISTS prompt_activations (
    id           BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name         TEXT NOT NULL,
    version      TEXT NOT NULL,
    activated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    actor        TEXT
);

CREATE INDEX IF NOT EXISTS idx_requests_created_at ON requests (created_at);
CREATE INDEX IF NOT EXISTS idx_requests_model ON requests (model);
CREATE INDEX IF NOT EXISTS idx_prompt_activations_at ON prompt_activations (activated_at);

-- Seed реєстру. Живе в міграції, а не в коді сервісу.
-- v1 навмисно без слова "support". На ньому mock відповідає «не знаю».
-- Це потрібно, щоб відтворити регресію і відкат.
INSERT INTO prompts (name, version, body, active) VALUES
    ('support-system', 'v1', 'You are an assistant.', false),
    ('support-system', 'v2', 'You are a support assistant. Be concise and helpful.', true)
ON CONFLICT (name, version) DO NOTHING;
