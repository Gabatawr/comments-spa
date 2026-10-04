-- =====================================================================
--  Schema: SPA «Комментарии» / Comments SPA
--  Target: MySQL 8.0+ / MariaDB 10.6+  (open in MySQL Workbench:
--          File -> Open SQL Script, or Server -> Data Import)
--
--  NOTE FOR REVIEWERS
--  ------------------
--  The running application uses SQLite by default (EF Core provider
--  Microsoft.EntityFrameworkCore.Sqlite) because the assignment allows
--  SQLite/PostgreSQL/MS SQL. This file is the *designed* relational
--  schema in MySQL dialect so it can be diffed against the implementation
--  in MySQL Workbench. The SQLite variant created by the committed EF Core
--  migration is logically identical (see db/schema.md for the type map).
--
--  Every column of the EF entities is present here, with the same names
--  (column name = EF property name in snake_case).
-- =====================================================================

DROP DATABASE IF EXISTS comments_spa;
CREATE DATABASE comments_spa
    CHARACTER SET utf8mb4
    COLLATE utf8mb4_unicode_ci;
USE comments_spa;

-- ---------------------------------------------------------------------
-- attachments: uploaded image (JPG/GIF/PNG, clamped to <= 320x240) or
--              text file (TXT, <= 100 KiB). Created before `comments`
--              because `comments.attachment_id` references it.
-- ---------------------------------------------------------------------
CREATE TABLE attachments (
    id               BIGINT UNSIGNED NOT NULL AUTO_INCREMENT
                     COMMENT 'PK',
    file_name        VARCHAR(255)    NOT NULL
                     COMMENT 'Original client file name',
    stored_name      VARCHAR(255)    NOT NULL
                     COMMENT 'Name on disk (randomised, no user input)',
    storage_path     VARCHAR(512)    NOT NULL
                     COMMENT 'Relative path under Storage:Root',
    content_type     VARCHAR(100)    NOT NULL
                     COMMENT 'image/png | image/jpeg | image/gif | text/plain',
    kind             ENUM('image','text') NOT NULL
                     COMMENT 'Attachment category',
    size_bytes       BIGINT UNSIGNED NOT NULL
                     COMMENT 'Size of the stored (post-resize) file',
    width            INT             NULL
                     COMMENT 'Final width after proportional clamp, NULL for text',
    height           INT             NULL
                     COMMENT 'Final height after proportional clamp, NULL for text',
    original_width   INT             NULL
                     COMMENT 'Width as uploaded, NULL for text',
    original_height  INT             NULL
                     COMMENT 'Height as uploaded, NULL for text',
    sha256           CHAR(64)        NOT NULL
                     COMMENT 'SHA-256 of stored bytes (integrity / dedup)',
    created_at       DATETIME(6)     NOT NULL
                     COMMENT 'UTC creation timestamp',
    PRIMARY KEY (id),
    KEY ix_attachments_kind (kind),
    KEY ix_attachments_created_at (created_at)
) ENGINE=InnoDB
  DEFAULT CHARSET=utf8mb4
  COLLATE=utf8mb4_unicode_ci
  COMMENT='Uploaded images and TXT files linked to comments';

-- ---------------------------------------------------------------------
-- comments: self-referencing tree. parent_id IS NULL -> root comment
--           (shown in the sortable/paginated table); otherwise a reply
--           (shown in the cascading thread under its root).
-- ---------------------------------------------------------------------
CREATE TABLE comments (
    id             BIGINT UNSIGNED NOT NULL AUTO_INCREMENT
                   COMMENT 'PK',
    parent_id      BIGINT UNSIGNED NULL
                   COMMENT 'Self FK; NULL = root comment, else parent comment',
    user_name      VARCHAR(50)     NOT NULL
                   COMMENT 'Only latin letters and digits, 3..50 (client+server validated)',
    email          VARCHAR(100)    NOT NULL
                   COMMENT 'Valid e-mail, <=100',
    home_page      VARCHAR(200)    NULL
                   COMMENT 'Optional absolute http(s) URL',
    text_html      TEXT            NOT NULL
                   COMMENT 'Sanitised HTML: allowlist a[href,title], code, i, strong; valid XHTML',
    text_plain     TEXT            NOT NULL
                   COMMENT 'Tag-stripped text (preview / table excerpt)',
    created_at     DATETIME(6)     NOT NULL
                   COMMENT 'UTC; default sort is LIFO = created_at DESC',
    client_ip      VARCHAR(64)     NULL
                   COMMENT 'Client identification: remote IP / X-Forwarded-For',
    user_agent     VARCHAR(512)    NULL
                   COMMENT 'Client identification: User-Agent header',
    attachment_id  BIGINT UNSIGNED NULL
                   COMMENT 'Optional single attachment (image OR text)',
    PRIMARY KEY (id),
    KEY ix_comments_parent_id (parent_id),
    KEY ix_comments_created_at (created_at),
    KEY ix_comments_user_name (user_name),
    KEY ix_comments_email (email),
    CONSTRAINT fk_comments_parent
        FOREIGN KEY (parent_id) REFERENCES comments (id)
        ON DELETE CASCADE,
    CONSTRAINT fk_comments_attachment
        FOREIGN KEY (attachment_id) REFERENCES attachments (id)
        ON DELETE SET NULL
) ENGINE=InnoDB
  DEFAULT CHARSET=utf8mb4
  COLLATE=utf8mb4_unicode_ci
  COMMENT='User comments, tree via parent_id';

-- ---------------------------------------------------------------------
-- CAPTCHA is intentionally NOT persisted: it lives in IMemoryCache with a
-- 5-minute TTL and one-time consumption (see docs/ARCHITECTURE.md).
-- ---------------------------------------------------------------------

-- ---------------------------------------------------------------------
-- Convenience read models (optional; can be used for the Middle-level
-- read side / GraphQL resolvers).
-- ---------------------------------------------------------------------
CREATE OR REPLACE VIEW v_root_comments AS
SELECT c.id,
       c.user_name,
       c.email,
       c.home_page,
       c.text_plain,
       c.created_at,
       c.attachment_id,
       (SELECT COUNT(*) FROM comments r WHERE r.parent_id = c.id) AS reply_count
FROM comments c
WHERE c.parent_id IS NULL;

-- Sanity check after install: should return 0 rows on a fresh DB.
-- SELECT COUNT(*) AS roots FROM comments WHERE parent_id IS NULL;
