-- MIGRONDI:NAME=1791590400000_name_colors.sql
-- MIGRONDI:TIMESTAMP=1791590400000
-- ---------- MIGRONDI:UP ----------
-- The color of the player's name in chat, 0xRRGGBB. Every existing account
-- gets one at random from the palette new accounts get theirs from
-- (NameColor.palette); the player chooses another in the game.
ALTER TABLE profiles ADD COLUMN name_color INTEGER NOT NULL DEFAULT 0 CHECK (name_color BETWEEN 0 AND 16777215);
UPDATE profiles SET name_color = CASE abs(random()) % 16
    WHEN 0 THEN 15037299 WHEN 1 THEN 15753874 WHEN 2 THEN 12216520 WHEN 3 THEN 9795021
    WHEN 4 THEN 7964363 WHEN 5 THEN 6600182 WHEN 6 THEN 5227511 WHEN 7 THEN 5099745
    WHEN 8 THEN 5093036 WHEN 9 THEN 8505220 WHEN 10 THEN 11457921 WHEN 11 THEN 14477173
    WHEN 12 THEN 16773494 WHEN 13 THEN 16766287 WHEN 14 THEN 16758605 ELSE 16747109 END;
PRAGMA user_version = 14;
-- ---------- MIGRONDI:DOWN ----------
ALTER TABLE profiles DROP COLUMN name_color;
PRAGMA user_version = 13;
