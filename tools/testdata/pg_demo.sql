-- ============================================================================
--  WebDbViewer :: демонстрационная/тестовая база PostgreSQL
--  Цель: покрыть ВСЕ типы объектов PostgreSQL, которые дерево объектов
--        и редактор данных должны уметь показывать и редактировать.
--
--  Запуск (база webdbviewer_demo должна существовать):
--    psql -h localhost -U postgres -d webdbviewer_demo -v ON_ERROR_STOP=1 -f pg_demo.sql
--
--  Скрипт идемпотентен: полностью пересоздаёт схемы demo_core / demo_extra
--  и свои объекты в public. Базу dbviewer_db (метахранилище приложения)
--  НЕ трогает.
-- ============================================================================

\set ON_ERROR_STOP on
-- Пароль для user mapping postgres_fdw (петля на этот же сервер).
-- Переопределяется так: psql -v fdw_password=xxx ...
\if :{?fdw_password}
\else
\set fdw_password '1'
\endif

SET client_encoding = 'UTF8';
SET client_min_messages = warning;

-- ----------------------------------------------------------------------------
-- 0. Очистка предыдущего запуска
-- ----------------------------------------------------------------------------
DROP EVENT TRIGGER IF EXISTS demo_ddl_audit;
DROP PUBLICATION IF EXISTS demo_pub;
DROP SERVER IF EXISTS demo_fdw_server CASCADE;
DROP SCHEMA IF EXISTS demo_core CASCADE;
DROP SCHEMA IF EXISTS demo_extra CASCADE;
DROP TABLE IF EXISTS public.public_notes CASCADE;
DROP VIEW IF EXISTS public.public_notes_v CASCADE;

-- ----------------------------------------------------------------------------
-- 1. Расширения (EXTENSION)
-- ----------------------------------------------------------------------------
CREATE EXTENSION IF NOT EXISTS pg_trgm      WITH SCHEMA public;
CREATE EXTENSION IF NOT EXISTS btree_gist   WITH SCHEMA public;
CREATE EXTENSION IF NOT EXISTS hstore       WITH SCHEMA public;
CREATE EXTENSION IF NOT EXISTS ltree        WITH SCHEMA public;
CREATE EXTENSION IF NOT EXISTS unaccent     WITH SCHEMA public;
CREATE EXTENSION IF NOT EXISTS postgres_fdw WITH SCHEMA public;

-- ----------------------------------------------------------------------------
-- 2. Роли (ROLE) — cluster-wide, поэтому создаём только если их нет
-- ----------------------------------------------------------------------------
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'demo_reader') THEN
        CREATE ROLE demo_reader NOLOGIN;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'demo_writer') THEN
        CREATE ROLE demo_writer NOLOGIN;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'demo_app') THEN
        CREATE ROLE demo_app LOGIN PASSWORD 'demo_app';
    END IF;
END
$$;

-- ----------------------------------------------------------------------------
-- 3. Схемы (SCHEMA)
-- ----------------------------------------------------------------------------
CREATE SCHEMA demo_core;
CREATE SCHEMA demo_extra;

COMMENT ON SCHEMA demo_core  IS 'Основная демо-схема: таблицы, представления, процедуры';
COMMENT ON SCHEMA demo_extra IS 'Дополнительная схема: экзотические объекты и одноимённые сущности';

SET search_path = demo_core, demo_extra, public;

-- ----------------------------------------------------------------------------
-- 4. Пользовательские типы: ENUM, COMPOSITE, RANGE, DOMAIN
-- ----------------------------------------------------------------------------
CREATE TYPE demo_core.order_status AS ENUM ('new', 'paid', 'shipped', 'cancelled');
COMMENT ON TYPE demo_core.order_status IS 'Перечисление: статус заказа';

CREATE TYPE demo_core.address AS (
    country text,
    city    text,
    street  text,
    zip     text
);
COMMENT ON TYPE demo_core.address IS 'Составной тип: почтовый адрес';

CREATE TYPE demo_core.money_range AS RANGE (
    subtype = numeric
);
COMMENT ON TYPE demo_core.money_range IS 'Диапазонный тип над numeric (даёт и multirange money_multirange)';

CREATE DOMAIN demo_core.email AS text
    CHECK (VALUE ~ '^[^@\s]+@[^@\s]+\.[^@\s]+$');
COMMENT ON DOMAIN demo_core.email IS 'Домен: адрес электронной почты с проверкой формата';

CREATE DOMAIN demo_core.positive_amount AS numeric(18, 2)
    NOT NULL
    DEFAULT 0
    CHECK (VALUE >= 0);

-- ----------------------------------------------------------------------------
-- 5. Последовательности (SEQUENCE)
-- ----------------------------------------------------------------------------
-- Отдельно стоящая последовательность
CREATE SEQUENCE demo_core.invoice_number_seq
    START WITH 1000
    INCREMENT BY 5
    MINVALUE 1000
    MAXVALUE 999999
    CACHE 10
    CYCLE;
COMMENT ON SEQUENCE demo_core.invoice_number_seq IS 'Самостоятельная последовательность номеров счетов';

CREATE SEQUENCE demo_extra.audit_seq AS bigint START WITH 1;

-- ----------------------------------------------------------------------------
-- 6. Таблицы (TABLE)
-- ----------------------------------------------------------------------------

-- 6.1 Обычная таблица: identity, generated column, ограничения, комментарии
CREATE TABLE demo_core.customers (
    id            integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    code          varchar(16)  NOT NULL,
    first_name    text         NOT NULL,
    last_name     text         NOT NULL,
    full_name     text GENERATED ALWAYS AS (first_name || ' ' || last_name) STORED,
    email         demo_core.email,
    billing       demo_core.address,
    tags          text[]       NOT NULL DEFAULT '{}',
    balance       demo_core.positive_amount,
    is_active     boolean      NOT NULL DEFAULT true,
    manager_id    integer      REFERENCES demo_core.customers (id) ON DELETE SET NULL,
    created_at    timestamptz  NOT NULL DEFAULT now(),
    metadata      jsonb        NOT NULL DEFAULT '{}'::jsonb,
    CONSTRAINT uq_customers_code   UNIQUE (code),
    CONSTRAINT ck_customers_names  CHECK (length(first_name) > 0 AND length(last_name) > 0)
);

COMMENT ON TABLE  demo_core.customers            IS 'Клиенты — основная таблица для тестов редактирования';
COMMENT ON COLUMN demo_core.customers.full_name  IS 'Вычисляемая колонка (GENERATED ALWAYS ... STORED)';
COMMENT ON COLUMN demo_core.customers.manager_id IS 'Ссылка на саму себя (self-referencing FK)';

-- 6.2 Таблица с serial (последовательность, принадлежащая колонке)
CREATE TABLE demo_core.products (
    id          serial PRIMARY KEY,
    sku         text        NOT NULL UNIQUE,
    title       text        NOT NULL,
    price       numeric(12, 2) NOT NULL CHECK (price >= 0),
    currency    char(3)     NOT NULL DEFAULT 'RUB',
    attributes  jsonb       NOT NULL DEFAULT '{}'::jsonb,
    search_text tsvector,
    weight_kg   double precision,
    discontinued boolean    NOT NULL DEFAULT false
);
COMMENT ON TABLE demo_core.products IS 'Товары; id на основе serial (owned sequence)';

-- 6.3 Заказы: FK с CASCADE, enum, ссылка на составной тип
CREATE TABLE demo_core.orders (
    id           bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
    customer_id  integer NOT NULL REFERENCES demo_core.customers (id) ON DELETE CASCADE,
    status       demo_core.order_status NOT NULL DEFAULT 'new',
    total        numeric(14, 2) NOT NULL DEFAULT 0,
    shipping     demo_core.address,
    placed_at    timestamptz NOT NULL DEFAULT now(),
    note         text
);

CREATE TABLE demo_core.order_items (
    order_id   bigint  NOT NULL REFERENCES demo_core.orders (id) ON DELETE CASCADE,
    product_id integer NOT NULL REFERENCES demo_core.products (id) ON DELETE RESTRICT,
    qty        integer NOT NULL CHECK (qty > 0),
    price      numeric(12, 2) NOT NULL,
    line_total numeric(14, 2) GENERATED ALWAYS AS (qty * price) STORED,
    CONSTRAINT pk_order_items PRIMARY KEY (order_id, product_id)
);
COMMENT ON TABLE demo_core.order_items IS 'Составной первичный ключ (тест редактирования по многоколоночному PK)';

-- 6.4 Таблица БЕЗ первичного ключа — негативный сценарий для редактора данных
CREATE TABLE demo_core.no_pk_readonly (
    event_time timestamptz NOT NULL DEFAULT now(),
    source     text,
    payload    jsonb
);
COMMENT ON TABLE demo_core.no_pk_readonly IS 'Без PK: редактирование строк должно быть недоступно';

-- 6.5 UNLOGGED таблица
CREATE UNLOGGED TABLE demo_core.session_cache (
    session_id uuid PRIMARY KEY,
    payload    jsonb,
    expires_at timestamptz
);
COMMENT ON TABLE demo_core.session_cache IS 'UNLOGGED таблица';

-- 6.6 Секционированная таблица (RANGE) + секции + DEFAULT-секция
CREATE TABLE demo_core.measurements (
    id        bigint GENERATED ALWAYS AS IDENTITY,
    device_id integer     NOT NULL,
    taken_at  date        NOT NULL,
    value     numeric(10, 3) NOT NULL,
    PRIMARY KEY (id, taken_at)
) PARTITION BY RANGE (taken_at);

CREATE TABLE demo_core.measurements_2024 PARTITION OF demo_core.measurements
    FOR VALUES FROM ('2024-01-01') TO ('2025-01-01');
CREATE TABLE demo_core.measurements_2025 PARTITION OF demo_core.measurements
    FOR VALUES FROM ('2025-01-01') TO ('2026-01-01');
CREATE TABLE demo_core.measurements_default PARTITION OF demo_core.measurements DEFAULT;

COMMENT ON TABLE demo_core.measurements IS 'Секционированная по диапазону дат таблица (3 секции)';

-- 6.7 Секционирование по списку
CREATE TABLE demo_core.logs (
    id       bigint GENERATED ALWAYS AS IDENTITY,
    level    text NOT NULL,
    message  text,
    PRIMARY KEY (id, level)
) PARTITION BY LIST (level);

CREATE TABLE demo_core.logs_info  PARTITION OF demo_core.logs FOR VALUES IN ('info', 'debug');
CREATE TABLE demo_core.logs_error PARTITION OF demo_core.logs FOR VALUES IN ('error', 'fatal');

-- 6.8 Наследование (INHERITS)
-- id объявлен через serial (а не IDENTITY): наследники получают DEFAULT nextval,
-- тогда как свойство IDENTITY при INHERITS не наследуется.
CREATE TABLE demo_core.vehicles (
    id        serial PRIMARY KEY,
    vin       text NOT NULL,
    made_year smallint
);

CREATE TABLE demo_core.trucks (
    payload_kg numeric(10, 2)
) INHERITS (demo_core.vehicles);
COMMENT ON TABLE demo_core.trucks IS 'Наследуется от demo_core.vehicles (INHERITS)';

-- 6.9 Exclusion constraint (требует btree_gist)
CREATE TABLE demo_core.room_bookings (
    id      integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    room_id integer   NOT NULL,
    during  tstzrange NOT NULL,
    guest   text      NOT NULL,
    EXCLUDE USING gist (room_id WITH =, during WITH &&)
);
COMMENT ON TABLE demo_core.room_bookings IS 'EXCLUDE-ограничение по пересечению интервалов';

-- 6.10 «Зоопарк типов» — для проверки грида, кодировок и форматирования
CREATE TABLE demo_core.type_zoo (
    id             integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    c_smallint     smallint,
    c_integer      integer,
    c_bigint       bigint,
    c_numeric      numeric(20, 8),
    c_real         real,
    c_double       double precision,
    c_money        money,
    c_char         char(5),
    c_varchar      varchar(64),
    c_text         text,
    c_bytea        bytea,
    c_date         date,
    c_time         time,
    c_timetz       time with time zone,
    c_timestamp    timestamp,
    c_timestamptz  timestamptz,
    c_interval     interval,
    c_boolean      boolean,
    c_uuid         uuid,
    c_json         json,
    c_jsonb        jsonb,
    c_xml          xml,
    c_int_array    integer[],
    c_text_array   text[],
    c_int2d        integer[][],
    c_point        point,
    c_lseg         lseg,
    c_box          box,
    c_path         path,
    c_polygon      polygon,
    c_circle       circle,
    c_cidr         cidr,
    c_inet         inet,
    c_macaddr      macaddr,
    c_macaddr8     macaddr8,
    c_bit          bit(8),
    c_varbit       varbit(16),
    c_tsvector     tsvector,
    c_tsquery      tsquery,
    c_int4range    int4range,
    c_nummultirange nummultirange,
    c_enum         demo_core.order_status,
    c_composite    demo_core.address,
    c_domain       demo_core.email,
    c_hstore       public.hstore,
    c_ltree        public.ltree
);
COMMENT ON TABLE demo_core.type_zoo IS 'Все базовые типы данных PostgreSQL в одной таблице';

-- 6.11 Одноимённые таблицы в разных схемах (проверка scope/квалификации имён)
CREATE TABLE demo_core.settings (
    key   text PRIMARY KEY,
    value text
);
CREATE TABLE demo_extra.settings (
    key   text PRIMARY KEY,
    value text,
    scope text NOT NULL DEFAULT 'extra'
);

-- 6.12 Таблица в public + кириллические/«кавычечные» идентификаторы
CREATE TABLE public.public_notes (
    id      serial PRIMARY KEY,
    "Заголовок" text NOT NULL,
    "текст"     text,
    created timestamptz NOT NULL DEFAULT now()
);
COMMENT ON TABLE public.public_notes IS 'Таблица в public с идентификаторами в кавычках и кириллице';

-- 6.13 Таблица c RLS
CREATE TABLE demo_extra.secured_docs (
    id       integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    owner    name    NOT NULL DEFAULT current_user,
    title    text    NOT NULL,
    body     text,
    secret   boolean NOT NULL DEFAULT false
);
ALTER TABLE demo_extra.secured_docs ENABLE ROW LEVEL SECURITY;

CREATE POLICY p_secured_docs_own ON demo_extra.secured_docs
    FOR SELECT TO demo_reader
    USING (owner = current_user);

CREATE POLICY p_secured_docs_write ON demo_extra.secured_docs
    FOR ALL TO demo_writer
    USING (NOT secret)
    WITH CHECK (NOT secret);

COMMENT ON TABLE demo_extra.secured_docs IS 'Row Level Security: две политики';

-- ----------------------------------------------------------------------------
-- 7. Индексы (INDEX)
-- ----------------------------------------------------------------------------
CREATE INDEX ix_customers_last_name        ON demo_core.customers (last_name);                       -- btree
CREATE INDEX ix_customers_name_created     ON demo_core.customers (last_name, created_at DESC);      -- многоколоночный
CREATE UNIQUE INDEX ux_customers_email_low ON demo_core.customers (lower(email::text));              -- уникальный по выражению
CREATE INDEX ix_customers_active           ON demo_core.customers (created_at) WHERE is_active;      -- частичный
CREATE INDEX ix_customers_metadata_gin     ON demo_core.customers USING gin (metadata jsonb_path_ops);
CREATE INDEX ix_customers_tags_gin         ON demo_core.customers USING gin (tags);
CREATE INDEX ix_orders_customer_incl       ON demo_core.orders (customer_id) INCLUDE (total, status); -- INCLUDE
CREATE INDEX ix_products_title_trgm        ON demo_core.products USING gin (title public.gin_trgm_ops);
CREATE INDEX ix_products_search            ON demo_core.products USING gin (search_text);
CREATE INDEX ix_orders_placed_brin         ON demo_core.orders USING brin (placed_at);
CREATE INDEX ix_products_price_hash        ON demo_core.products USING hash (price);

-- ----------------------------------------------------------------------------
-- 8. Функции (FUNCTION)
-- ----------------------------------------------------------------------------

-- 8.1 Простая SQL-функция, IMMUTABLE
CREATE FUNCTION demo_core.fn_full_name(p_first text, p_last text)
RETURNS text
LANGUAGE sql
IMMUTABLE
AS $$ SELECT p_first || ' ' || p_last $$;
COMMENT ON FUNCTION demo_core.fn_full_name(text, text) IS 'SQL-функция, IMMUTABLE';

-- 8.2 PL/pgSQL функция с ветвлениями
CREATE FUNCTION demo_core.fn_order_discount(p_total numeric, p_status demo_core.order_status)
RETURNS numeric
LANGUAGE plpgsql
STABLE
AS $$
DECLARE
    v_rate numeric := 0;
BEGIN
    IF p_status = 'cancelled' THEN
        RETURN 0;
    ELSIF p_total >= 100000 THEN
        v_rate := 0.15;
    ELSIF p_total >= 10000 THEN
        v_rate := 0.07;
    ELSE
        v_rate := 0.00;
    END IF;

    RETURN round(p_total * v_rate, 2);
END;
$$;

-- 8.3 Перегруженные функции (одно имя, разные сигнатуры)
CREATE FUNCTION demo_core.fn_lookup(p_id integer)
RETURNS text LANGUAGE sql STABLE
AS $$ SELECT full_name FROM demo_core.customers WHERE id = p_id $$;

CREATE FUNCTION demo_core.fn_lookup(p_code text)
RETURNS text LANGUAGE sql STABLE
AS $$ SELECT full_name FROM demo_core.customers WHERE code = p_code $$;

CREATE FUNCTION demo_core.fn_lookup(p_id integer, p_with_email boolean)
RETURNS text LANGUAGE sql STABLE
AS $$
    SELECT CASE WHEN p_with_email THEN full_name || ' <' || coalesce(email::text, '') || '>'
                ELSE full_name END
    FROM demo_core.customers WHERE id = p_id
$$;

-- 8.4 Функция, возвращающая TABLE
CREATE FUNCTION demo_core.fn_customer_orders(p_customer_id integer)
RETURNS TABLE (order_id bigint, status demo_core.order_status, total numeric)
LANGUAGE sql STABLE
AS $$
    SELECT o.id, o.status, o.total
    FROM demo_core.orders o
    WHERE o.customer_id = p_customer_id
    ORDER BY o.placed_at DESC
$$;

-- 8.5 Функция, возвращающая SETOF составного типа
CREATE FUNCTION demo_core.fn_top_products(p_limit integer DEFAULT 5)
RETURNS SETOF demo_core.products
LANGUAGE sql STABLE
AS $$ SELECT * FROM demo_core.products ORDER BY price DESC LIMIT p_limit $$;

-- 8.6 VARIADIC + OUT-параметры
CREATE FUNCTION demo_core.fn_concat_all(VARIADIC p_parts text[])
RETURNS text LANGUAGE sql IMMUTABLE
AS $$ SELECT array_to_string(p_parts, ', ') $$;

CREATE FUNCTION demo_core.fn_split_name(
    IN  p_full  text,
    OUT o_first text,
    OUT o_last  text)
LANGUAGE plpgsql IMMUTABLE
AS $$
BEGIN
    o_first := split_part(p_full, ' ', 1);
    o_last  := split_part(p_full, ' ', 2);
END;
$$;

-- 8.7 Функция, возвращающая курсор (refcursor)
CREATE FUNCTION demo_core.fn_open_customers(p_cursor refcursor)
RETURNS refcursor
LANGUAGE plpgsql
AS $$
BEGIN
    OPEN p_cursor FOR SELECT id, full_name FROM demo_core.customers ORDER BY id;
    RETURN p_cursor;
END;
$$;

-- 8.8 Триггерные функции
CREATE FUNCTION demo_core.trg_products_search_vector()
RETURNS trigger LANGUAGE plpgsql
AS $$
BEGIN
    NEW.search_text := to_tsvector('simple', coalesce(NEW.title, '') || ' ' || coalesce(NEW.sku, ''));
    RETURN NEW;
END;
$$;

CREATE FUNCTION demo_core.trg_orders_recalc_total()
RETURNS trigger LANGUAGE plpgsql
AS $$
BEGIN
    UPDATE demo_core.orders o
       SET total = coalesce((SELECT sum(line_total) FROM demo_core.order_items i WHERE i.order_id = o.id), 0)
     WHERE o.id = coalesce(NEW.order_id, OLD.order_id);
    RETURN NULL;
END;
$$;

CREATE FUNCTION demo_core.trg_stmt_audit()
RETURNS trigger LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO demo_extra.change_log (table_name, operation)
    VALUES (TG_TABLE_SCHEMA || '.' || TG_TABLE_NAME, TG_OP);
    RETURN NULL;
END;
$$;

CREATE FUNCTION demo_core.trg_check_order_total()
RETURNS trigger LANGUAGE plpgsql
AS $$
BEGIN
    IF NEW.total < 0 THEN
        RAISE EXCEPTION 'Сумма заказа % не может быть отрицательной', NEW.id;
    END IF;
    RETURN NEW;
END;
$$;

CREATE FUNCTION demo_core.trg_view_instead_of_insert()
RETURNS trigger LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO demo_core.customers (code, first_name, last_name, email)
    VALUES (NEW.code, NEW.first_name, NEW.last_name, NEW.email);
    RETURN NEW;
END;
$$;

-- 8.9 Функция для агрегата и сам агрегат (AGGREGATE)
CREATE FUNCTION demo_core.agg_sum_positive_sfunc(state numeric, val numeric)
RETURNS numeric LANGUAGE sql IMMUTABLE STRICT
AS $$ SELECT state + GREATEST(val, 0) $$;

CREATE AGGREGATE demo_core.sum_positive(numeric) (
    SFUNC    = demo_core.agg_sum_positive_sfunc,
    STYPE    = numeric,
    INITCOND = '0'
);
COMMENT ON AGGREGATE demo_core.sum_positive(numeric) IS 'Пользовательская агрегатная функция';

-- 8.10 Функция и оператор (OPERATOR)
CREATE FUNCTION demo_extra.fn_approx_eq(numeric, numeric)
RETURNS boolean LANGUAGE sql IMMUTABLE STRICT
AS $$ SELECT abs($1 - $2) < 0.01 $$;

CREATE OPERATOR demo_extra.=~= (
    LEFTARG    = numeric,
    RIGHTARG   = numeric,
    FUNCTION   = demo_extra.fn_approx_eq,
    COMMUTATOR = OPERATOR(demo_extra.=~=)
);

-- 8.11 Функция для событийного триггера
CREATE TABLE demo_extra.ddl_log (
    id        bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    happened  timestamptz NOT NULL DEFAULT now(),
    tag       text,
    object_id text
);

CREATE FUNCTION demo_extra.fn_ddl_audit()
RETURNS event_trigger LANGUAGE plpgsql
AS $$
DECLARE
    r record;
BEGIN
    FOR r IN SELECT * FROM pg_event_trigger_ddl_commands() LOOP
        INSERT INTO demo_extra.ddl_log (tag, object_id) VALUES (r.command_tag, r.object_identity);
    END LOOP;
END;
$$;

-- ----------------------------------------------------------------------------
-- 9. Процедуры (PROCEDURE)
-- ----------------------------------------------------------------------------
CREATE PROCEDURE demo_core.sp_add_customer(
    IN  p_code  varchar(16),
    IN  p_first text,
    IN  p_last  text,
    IN  p_email demo_core.email DEFAULT NULL)
LANGUAGE plpgsql
AS $$
BEGIN
    INSERT INTO demo_core.customers (code, first_name, last_name, email)
    VALUES (p_code, p_first, p_last, p_email);
END;
$$;
COMMENT ON PROCEDURE demo_core.sp_add_customer(varchar, text, text, demo_core.email) IS 'Процедура добавления клиента';

CREATE PROCEDURE demo_core.sp_place_order(
    INOUT p_order_id   bigint,
    IN    p_customer_id integer,
    IN    p_product_id  integer,
    IN    p_qty         integer)
LANGUAGE plpgsql
AS $$
DECLARE
    v_price numeric(12, 2);
BEGIN
    SELECT price INTO v_price FROM demo_core.products WHERE id = p_product_id;

    IF p_order_id IS NULL THEN
        INSERT INTO demo_core.orders (customer_id) VALUES (p_customer_id) RETURNING id INTO p_order_id;
    END IF;

    INSERT INTO demo_core.order_items (order_id, product_id, qty, price)
    VALUES (p_order_id, p_product_id, p_qty, v_price)
    ON CONFLICT (order_id, product_id) DO UPDATE SET qty = demo_core.order_items.qty + EXCLUDED.qty;
END;
$$;

-- Процедура с транзакционным управлением
CREATE PROCEDURE demo_extra.sp_cleanup_cache(IN p_older_than interval DEFAULT '1 day')
LANGUAGE plpgsql
AS $$
BEGIN
    DELETE FROM demo_core.session_cache WHERE expires_at < now() - p_older_than;
    COMMIT;
END;
$$;

-- Перегруженная процедура
CREATE PROCEDURE demo_extra.sp_cleanup_cache(IN p_before timestamptz)
LANGUAGE plpgsql
AS $$
BEGIN
    DELETE FROM demo_core.session_cache WHERE expires_at < p_before;
END;
$$;

-- ----------------------------------------------------------------------------
-- 10. Таблица журнала (нужна триггеру п.8.8) — создаём до триггеров
-- ----------------------------------------------------------------------------
CREATE TABLE demo_extra.change_log (
    id         bigint  NOT NULL DEFAULT nextval('demo_extra.audit_seq') PRIMARY KEY,
    changed_at timestamptz NOT NULL DEFAULT now(),
    table_name text    NOT NULL,
    operation  text    NOT NULL
);
COMMENT ON TABLE demo_extra.change_log IS 'Журнал изменений; id из независимой последовательности';

-- ----------------------------------------------------------------------------
-- 11. Представления (VIEW / MATERIALIZED VIEW)
-- ----------------------------------------------------------------------------

-- 11.1 Простое представление
CREATE VIEW demo_core.v_active_customers AS
SELECT id, code, full_name, email, balance, created_at
FROM demo_core.customers
WHERE is_active;
COMMENT ON VIEW demo_core.v_active_customers IS 'Обновляемое представление активных клиентов';

-- 11.2 Представление с WITH CHECK OPTION
CREATE VIEW demo_core.v_paid_orders AS
SELECT id, customer_id, status, total, placed_at
FROM demo_core.orders
WHERE status = 'paid'
WITH CASCADED CHECK OPTION;

-- 11.3 Представление с оконными функциями и агрегацией
CREATE VIEW demo_core.v_order_stats AS
SELECT o.customer_id,
       c.full_name,
       count(*)                                             AS orders_count,
       sum(o.total)                                         AS total_sum,
       avg(o.total)                                         AS total_avg,
       rank() OVER (ORDER BY sum(o.total) DESC)             AS rank_by_sum
FROM demo_core.orders o
JOIN demo_core.customers c ON c.id = o.customer_id
GROUP BY o.customer_id, c.full_name;

-- 11.4 Представление с INSTEAD OF триггером (необновляемое само по себе)
CREATE VIEW demo_core.v_customer_names AS
SELECT c.code,
       c.first_name,
       c.last_name,
       c.email,
       (SELECT count(*) FROM demo_core.orders o WHERE o.customer_id = c.id) AS orders_count
FROM demo_core.customers c;

-- 11.5 Рекурсивное представление (WITH RECURSIVE)
CREATE VIEW demo_core.v_customer_hierarchy AS
WITH RECURSIVE tree AS (
    SELECT id, manager_id, full_name, 1 AS level
    FROM demo_core.customers
    WHERE manager_id IS NULL
    UNION ALL
    SELECT c.id, c.manager_id, c.full_name, t.level + 1
    FROM demo_core.customers c
    JOIN tree t ON c.manager_id = t.id
)
SELECT * FROM tree;

-- 11.6 Кросс-схемное представление
CREATE VIEW demo_extra.v_settings_merged AS
SELECT 'core'::text AS origin, key, value FROM demo_core.settings
UNION ALL
SELECT 'extra'::text AS origin, key, value FROM demo_extra.settings;

CREATE VIEW public.public_notes_v AS
SELECT id, "Заголовок" AS title, "текст" AS body, created FROM public.public_notes;

-- 11.7 Материализованные представления
CREATE MATERIALIZED VIEW demo_core.mv_product_sales AS
SELECT p.id           AS product_id,
       p.sku,
       p.title,
       coalesce(sum(i.qty), 0)        AS sold_qty,
       coalesce(sum(i.line_total), 0) AS sold_amount
FROM demo_core.products p
LEFT JOIN demo_core.order_items i ON i.product_id = p.id
GROUP BY p.id, p.sku, p.title
WITH NO DATA;

CREATE MATERIALIZED VIEW demo_core.mv_daily_measurements AS
SELECT taken_at, device_id, avg(value) AS avg_value, count(*) AS samples
FROM demo_core.measurements
GROUP BY taken_at, device_id
WITH DATA;

CREATE UNIQUE INDEX ux_mv_daily_measurements ON demo_core.mv_daily_measurements (taken_at, device_id);
COMMENT ON MATERIALIZED VIEW demo_core.mv_daily_measurements IS 'Материализованное представление с уникальным индексом (поддерживает REFRESH CONCURRENTLY)';

-- ----------------------------------------------------------------------------
-- 12. Триггеры (TRIGGER)
-- ----------------------------------------------------------------------------

-- 12.1 BEFORE ROW
CREATE TRIGGER tg_products_search
    BEFORE INSERT OR UPDATE OF title, sku ON demo_core.products
    FOR EACH ROW
    EXECUTE FUNCTION demo_core.trg_products_search_vector();

-- 12.2 AFTER ROW на нескольких событиях
CREATE TRIGGER tg_order_items_total
    AFTER INSERT OR UPDATE OR DELETE ON demo_core.order_items
    FOR EACH ROW
    EXECUTE FUNCTION demo_core.trg_orders_recalc_total();

-- 12.3 Триггер уровня оператора (STATEMENT)
CREATE TRIGGER tg_customers_stmt_audit
    AFTER INSERT OR UPDATE OR DELETE ON demo_core.customers
    FOR EACH STATEMENT
    EXECUTE FUNCTION demo_core.trg_stmt_audit();

-- 12.4 Условный триггер (WHEN)
CREATE TRIGGER tg_orders_big_audit
    AFTER UPDATE ON demo_core.orders
    FOR EACH ROW
    WHEN (NEW.total > 50000)
    EXECUTE FUNCTION demo_core.trg_stmt_audit();

-- 12.5 CONSTRAINT TRIGGER (отложенный)
CREATE CONSTRAINT TRIGGER tg_orders_total_check
    AFTER INSERT OR UPDATE ON demo_core.orders
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW
    EXECUTE FUNCTION demo_core.trg_check_order_total();

-- 12.6 INSTEAD OF на представлении
CREATE TRIGGER tg_v_customer_names_ins
    INSTEAD OF INSERT ON demo_core.v_customer_names
    FOR EACH ROW
    EXECUTE FUNCTION demo_core.trg_view_instead_of_insert();

-- ----------------------------------------------------------------------------
-- 13. Правила (RULE)
-- ----------------------------------------------------------------------------
CREATE RULE r_no_delete_settings AS
    ON DELETE TO demo_core.settings
    DO INSTEAD NOTHING;

CREATE RULE r_notes_log_insert AS
    ON INSERT TO public.public_notes
    DO ALSO INSERT INTO demo_extra.change_log (table_name, operation)
        VALUES ('public.public_notes', 'RULE_INSERT');

-- ----------------------------------------------------------------------------
-- 14. Полнотекстовый поиск: словарь и конфигурация
-- ----------------------------------------------------------------------------
CREATE TEXT SEARCH DICTIONARY demo_extra.demo_dict (
    TEMPLATE  = pg_catalog.simple,
    STOPWORDS = english
);

CREATE TEXT SEARCH CONFIGURATION demo_extra.demo_tsconfig (COPY = pg_catalog.simple);

ALTER TEXT SEARCH CONFIGURATION demo_extra.demo_tsconfig
    ALTER MAPPING FOR asciiword, word WITH demo_extra.demo_dict;

COMMENT ON TEXT SEARCH CONFIGURATION demo_extra.demo_tsconfig IS 'Демонстрационная конфигурация FTS';

-- ----------------------------------------------------------------------------
-- 15. Правила сортировки (COLLATION)
-- ----------------------------------------------------------------------------
CREATE COLLATION demo_extra.demo_c FROM "C";

DO $$
BEGIN
    -- Регистронезависимая ICU-коллация; доступна не во всех сборках
    EXECUTE $ddl$CREATE COLLATION demo_extra.ru_ci (provider = icu, locale = 'ru-RU-u-ks-level2', deterministic = false)$ddl$;
EXCEPTION WHEN OTHERS THEN
    RAISE WARNING 'ICU-коллация не создана: %', SQLERRM;
END
$$;

-- ----------------------------------------------------------------------------
-- 16. Внешние данные (FOREIGN DATA WRAPPER / SERVER / FOREIGN TABLE)
-- ----------------------------------------------------------------------------
CREATE SERVER demo_fdw_server
    FOREIGN DATA WRAPPER postgres_fdw
    OPTIONS (host 'localhost', port '5432', dbname 'webdbviewer_demo');

CREATE USER MAPPING FOR CURRENT_USER
    SERVER demo_fdw_server
    OPTIONS (user 'postgres', password :'fdw_password');

CREATE FOREIGN TABLE demo_extra.customers_remote (
    id         integer,
    code       varchar(16),
    full_name  text,
    email      text
)
SERVER demo_fdw_server
OPTIONS (schema_name 'demo_core', table_name 'customers');

COMMENT ON FOREIGN TABLE demo_extra.customers_remote IS 'Внешняя таблица через postgres_fdw (петля на эту же БД)';

-- ----------------------------------------------------------------------------
-- 17. Публикация (PUBLICATION) — объект логической репликации
-- ----------------------------------------------------------------------------
CREATE PUBLICATION demo_pub FOR TABLE demo_core.customers, demo_core.orders;

-- ----------------------------------------------------------------------------
-- 18. Привилегии (GRANT)
-- ----------------------------------------------------------------------------
GRANT USAGE ON SCHEMA demo_core, demo_extra TO demo_reader, demo_writer, demo_app;
GRANT SELECT ON ALL TABLES IN SCHEMA demo_core, demo_extra TO demo_reader;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA demo_core TO demo_writer;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA demo_core, demo_extra TO demo_writer;
GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA demo_core TO demo_reader;
GRANT demo_reader TO demo_app;

ALTER DEFAULT PRIVILEGES IN SCHEMA demo_core GRANT SELECT ON TABLES TO demo_reader;

-- ----------------------------------------------------------------------------
-- 19. Данные
-- ----------------------------------------------------------------------------

INSERT INTO demo_core.customers (code, first_name, last_name, email, billing, tags, balance, is_active, manager_id, metadata)
VALUES
 ('C-001', 'Иван',    'Петров',   'ivan.petrov@example.com',   ROW('Россия','Москва','ул. Тверская, 1','101000')::demo_core.address,  ARRAY['vip','опт'],       15000.50, true,  NULL, '{"segment":"vip","score":97}'),
 ('C-002', 'Мария',   'Иванова',  'maria.ivanova@example.com', ROW('Россия','Санкт-Петербург','Невский пр., 28','191186')::demo_core.address, ARRAY['розница'],  250.00,   true,  1,    '{"segment":"retail","score":45}'),
 ('C-003', 'Давron',  'Tashkent', 'davron@example.uz',         ROW('Oʻzbekiston','Toshkent','Amir Temur ko''chasi, 5','100000')::demo_core.address, ARRAY['экспорт','🌍'], 0.00, true, 1, '{"segment":"export","tags":["new"]}'),
 ('C-004', 'John',    'O''Reilly','john.oreilly@example.com',  ROW('USA','New York','5th Ave, 100','10001')::demo_core.address,       ARRAY['wholesale'],       98765.43, false, 1,    '{"segment":"wholesale"}'),
 ('C-005', '张',      '伟',        'zhang.wei@example.cn',      ROW('中国','北京','长安街 1 号','100000')::demo_core.address,          ARRAY['asia','测试'],      1.01,     true,  2,    '{"segment":"asia","unicode":true}');

INSERT INTO demo_core.products (sku, title, price, currency, attributes, weight_kg, discontinued)
VALUES
 ('SKU-100', 'Ноутбук «Аврора» 15"',        89990.00, 'RUB', '{"cpu":"i7","ram_gb":16}',      1.85,  false),
 ('SKU-101', 'Мышь беспроводная',             1490.00, 'RUB', '{"dpi":1600,"wireless":true}',  0.09,  false),
 ('SKU-102', 'Клавиатура механическая RGB',   7990.00, 'RUB', '{"switches":"brown"}',          0.95,  false),
 ('SKU-103', 'Монитор 27" 4K',              45990.00, 'RUB', '{"panel":"IPS","hz":60}',       5.40,  false),
 ('SKU-104', 'Кабель USB-C (снят с продажи)',  590.00, 'RUB', '{"length_m":1}',                0.05,  true);

INSERT INTO demo_core.orders (customer_id, status, placed_at, note)
VALUES
 (1, 'paid',      now() - interval '10 days', 'Оплачен картой'),
 (1, 'shipped',   now() - interval '3 days',  NULL),
 (2, 'new',       now() - interval '1 day',   'Ожидает оплаты'),
 (3, 'cancelled', now() - interval '30 days', 'Отменён клиентом'),
 (5, 'paid',      now() - interval '2 hours', '测试订单');

INSERT INTO demo_core.order_items (order_id, product_id, qty, price)
VALUES
 (1, 1, 1, 89990.00),
 (1, 2, 2,  1490.00),
 (2, 3, 1,  7990.00),
 (3, 4, 2, 45990.00),
 (5, 2, 5,  1490.00);

INSERT INTO demo_core.settings (key, value) VALUES
 ('theme', 'dark'), ('locale', 'ru-RU'), ('page.size', '100');

INSERT INTO demo_extra.settings (key, value, scope) VALUES
 ('theme', 'light', 'extra'), ('retention.days', '30', 'extra');

INSERT INTO public.public_notes ("Заголовок", "текст") VALUES
 ('Первая заметка', 'Текст с кириллицей, ёлкой и эмодзи 🎄'),
 ('Second note',    'Plain ASCII text'),
 ('Тест кодировки', E'Спецсимволы: \t табуляция, "кавычки", ''апострофы'', \\ обратный слэш');

INSERT INTO demo_core.measurements (device_id, taken_at, value) VALUES
 (1, '2024-03-15', 21.500), (1, '2024-03-16', 22.125), (2, '2024-07-01', 18.000),
 (1, '2025-01-10', 19.750), (2, '2025-06-30', 25.250),
 (3, '2023-12-31', 10.000), (3, '2026-01-01', 30.000);

INSERT INTO demo_core.logs (level, message) VALUES
 ('info', 'Приложение запущено'), ('debug', 'Кэш прогрет'),
 ('error', 'Не удалось подключиться'), ('fatal', 'Аварийное завершение');

INSERT INTO demo_core.vehicles (vin, made_year) VALUES ('VIN-AAA-0001', 2019), ('VIN-BBB-0002', 2022);
INSERT INTO demo_core.trucks   (vin, made_year, payload_kg) VALUES ('VIN-TRK-0003', 2021, 3500.00);

INSERT INTO demo_core.room_bookings (room_id, during, guest) VALUES
 (101, tstzrange('2026-01-10 12:00+00', '2026-01-12 10:00+00'), 'Иван Петров'),
 (101, tstzrange('2026-01-12 14:00+00', '2026-01-15 10:00+00'), 'Мария Иванова'),
 (102, tstzrange('2026-01-10 12:00+00', '2026-01-20 10:00+00'), 'John O''Reilly');

INSERT INTO demo_core.session_cache (session_id, payload, expires_at) VALUES
 ('11111111-1111-1111-1111-111111111111', '{"user":"admin"}', now() + interval '1 hour'),
 ('22222222-2222-2222-2222-222222222222', '{"user":"guest"}', now() - interval '1 hour');

INSERT INTO demo_core.no_pk_readonly (source, payload) VALUES
 ('scheduler', '{"job":"cleanup"}'), ('api', '{"path":"/health"}'), (NULL, NULL);

INSERT INTO demo_extra.secured_docs (title, body, secret) VALUES
 ('Публичный документ', 'Виден всем', false),
 ('Секретный документ', 'Только владельцу', true);

-- Зоопарк типов: строка со значениями и строка со всеми NULL
INSERT INTO demo_core.type_zoo (
    c_smallint, c_integer, c_bigint, c_numeric, c_real, c_double, c_money,
    c_char, c_varchar, c_text, c_bytea,
    c_date, c_time, c_timetz, c_timestamp, c_timestamptz, c_interval,
    c_boolean, c_uuid, c_json, c_jsonb, c_xml,
    c_int_array, c_text_array, c_int2d,
    c_point, c_lseg, c_box, c_path, c_polygon, c_circle,
    c_cidr, c_inet, c_macaddr, c_macaddr8, c_bit, c_varbit,
    c_tsvector, c_tsquery, c_int4range, c_nummultirange,
    c_enum, c_composite, c_domain, c_hstore, c_ltree)
VALUES (
    32767, 2147483647, 9223372036854775807, 12345.67890123, 3.14, 2.718281828459045, 1234.56::money,
    'ABCDE', 'varchar значение', E'Многострочный\nтекст с "кавычками" и \\ слэшем 🚀', '\xdeadbeef'::bytea,
    DATE '2024-02-29', TIME '23:59:59.123456', TIME WITH TIME ZONE '12:00:00+05', TIMESTAMP '2026-01-15 10:20:30.5', TIMESTAMPTZ '2026-01-15 10:20:30.5+03', INTERVAL '1 year 2 mons 3 days 04:05:06',
    true, '33333333-3333-3333-3333-333333333333', '{"a":[1,2,3]}', '{"b":{"c":true}}', XMLPARSE(DOCUMENT '<root><item id="1">значение</item></root>'),
    ARRAY[1,2,3], ARRAY['один','два','три'], ARRAY[[1,2],[3,4]],
    '(1,2)', '[(0,0),(1,1)]', '((0,0),(2,2))', '[(0,0),(1,1),(2,0)]', '((0,0),(2,0),(1,2))', '<(1,1),5>',
    '192.168.100.0/24', '10.0.0.1/32', '08:00:2b:01:02:03', '08:00:2b:01:02:03:04:05', B'10101010', B'1100',
    to_tsvector('simple', 'привет мир hello world'), to_tsquery('simple', 'привет & мир'), int4range(1, 10, '[)'), '{[1,5],[10,20)}'::nummultirange,
    'paid', ROW('Россия','Казань','ул. Баумана, 3','420000')::demo_core.address, 'zoo@example.com', 'key1=>value1, ключ2=>значение2'::public.hstore, 'top.middle.leaf'::public.ltree
);

INSERT INTO demo_core.type_zoo (c_text) VALUES (NULL);
INSERT INTO demo_core.type_zoo (c_smallint, c_integer, c_numeric, c_text, c_boolean)
VALUES (-32768, -2147483648, -0.00000001, '', false);

-- Материализованное представление, созданное WITH NO DATA
REFRESH MATERIALIZED VIEW demo_core.mv_product_sales;
REFRESH MATERIALIZED VIEW demo_core.mv_daily_measurements;

SELECT setval('demo_core.invoice_number_seq', 1050, true);

-- ----------------------------------------------------------------------------
-- 20. Событийный триггер (EVENT TRIGGER) — создаём последним
-- ----------------------------------------------------------------------------
CREATE EVENT TRIGGER demo_ddl_audit
    ON ddl_command_end
    WHEN TAG IN ('CREATE TABLE', 'ALTER TABLE', 'DROP TABLE', 'CREATE INDEX')
    EXECUTE FUNCTION demo_extra.fn_ddl_audit();

COMMENT ON EVENT TRIGGER demo_ddl_audit IS 'Пишет DDL-операции в demo_extra.ddl_log';

ANALYZE;
