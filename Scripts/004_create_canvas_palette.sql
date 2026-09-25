-- ============================================================================
-- Migration: 004_create_canvas_palette.sql
-- Description: Create 8-bit Canvas Palette table and seed all 256 curated colors.
--              First 32 colors are configured as active (is_active = 1).
--              Changing active colors is achieved purely by updating is_active.
-- ============================================================================

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'canvas_palette')
BEGIN
    CREATE TABLE canvas_palette (
        id TINYINT PRIMARY KEY,
        hex_code VARCHAR(7) NOT NULL,
        name NVARCHAR(50) NOT NULL,
        is_active BIT NOT NULL DEFAULT 0,
        sort_order INT NOT NULL DEFAULT 0,
        created_at DATETIMEOFFSET NOT NULL DEFAULT SYSDATETIMEOFFSET()
    );

    CREATE INDEX IX_canvas_palette_is_active ON canvas_palette(is_active, sort_order);
END
GO

-- Seed 256 colors idempotently
MERGE INTO canvas_palette AS target
USING (
    VALUES
    -- 0..15: Classic 16 colors (preserved for backward compatibility, active)
    (0, '#FFFFFF', 'Pure White', 1, 0),
    (1, '#E4E4E4', 'Light Gray', 1, 1),
    (2, '#888888', 'Medium Gray', 1, 2),
    (3, '#222222', 'Dark Charcoal', 1, 3),
    (4, '#FFA7D1', 'Light Pink', 1, 4),
    (5, '#E50000', 'Crimson Red', 1, 5),
    (6, '#E59500', 'Vivid Orange', 1, 6),
    (7, '#A06A42', 'Earth Brown', 1, 7),
    (8, '#E5D900', 'Sunflower Yellow', 1, 8),
    (9, '#94E044', 'Lime Green', 1, 9),
    (10, '#02BE01', 'Vibrant Green', 1, 10),
    (11, '#00D3DD', 'Aqua Cyan', 1, 11),
    (12, '#0083C7', 'Ocean Blue', 1, 12),
    (13, '#0000EA', 'Royal Blue', 1, 13),
    (14, '#CF6EE4', 'Soft Magenta', 1, 14),
    (15, '#820080', 'Deep Purple', 1, 15),

    -- 16..31: Curated New Active Colors (Completes Initial 32 Active Palette)
    (16, '#000000', 'Pure Black', 1, 16),
    (17, '#4A5568', 'Slate Gray', 1, 17),
    (18, '#6B1D2F', 'Deep Maroon', 1, 18),
    (19, '#FF6B6B', 'Coral Red', 1, 19),
    (20, '#FF8DA1', 'Blush Pink', 1, 20),
    (21, '#F6C492', 'Peach Skin', 1, 21),
    (22, '#FFF3CD', 'Warm Cream', 1, 22),
    (23, '#FFBF00', 'Amber Gold', 1, 23),
    (24, '#6B8E23', 'Olive Drab', 1, 24),
    (25, '#1B4D3E', 'Forest Green', 1, 25),
    (26, '#98FF98', 'Mint Green', 1, 26),
    (27, '#008080', 'Teal', 1, 27),
    (28, '#70C1B3', 'Seafoam Cyan', 1, 28),
    (29, '#4A69BD', 'Indigo', 1, 29),
    (30, '#9B59B6', 'Amethyst Purple', 1, 30),
    (31, '#5D4037', 'Coffee Brown', 1, 31),

    -- 32..47: Monochromes & Neutral Grays
    (32, '#0D0D0D', 'Obsidian', 0, 32),
    (33, '#1A1A1A', 'Jet Black', 0, 33),
    (34, '#2D3748', 'Charcoal', 0, 34),
    (35, '#333333', 'Dark Slate', 0, 35),
    (36, '#555555', 'Granite', 0, 36),
    (37, '#666666', 'Pewter', 0, 37),
    (38, '#777777', 'Ash Gray', 0, 38),
    (39, '#999999', 'Silver', 0, 39),
    (40, '#AAAAAA', 'Aluminum', 0, 40),
    (41, '#BBBBBB', 'Mist Gray', 0, 41),
    (42, '#CCCCCC', 'Platinum', 0, 42),
    (43, '#D6D6D6', 'Pale Gray', 0, 43),
    (44, '#EEEEEE', 'Off White', 0, 44),
    (45, '#F5F5F5', 'Smoke White', 0, 45),
    (46, '#F8F9FA', 'Ghost White', 0, 46),
    (47, '#FAF0E6', 'Linen', 0, 47),

    -- 48..63: Red & Crimson Spectrum
    (48, '#3B0000', 'Night Crimson', 0, 48),
    (49, '#550000', 'Dark Blood', 0, 49),
    (50, '#7A0000', 'Oxblood', 0, 50),
    (51, '#990000', 'Garnet', 0, 51),
    (52, '#B30000', 'Dark Red', 0, 52),
    (53, '#CC0000', 'Carmine Red', 0, 53),
    (54, '#FF0033', 'Electric Red', 0, 54),
    (55, '#FF2400', 'Scarlet', 0, 55),
    (56, '#D32F2F', 'Firebrick', 0, 56),
    (57, '#C2185B', 'Dark Rose', 0, 57),
    (58, '#AD1457', 'Boysenberry', 0, 58),
    (59, '#880E4F', 'Velvet Crimson', 0, 59),
    (60, '#E91E63', 'Cherry', 0, 60),
    (61, '#FF4081', 'Hot Coral', 0, 61),
    (62, '#FF5252', 'Radium Red', 0, 62),
    (63, '#FF7961', 'Salmon Red', 0, 63),

    -- 64..79: Warm Tones, Terracotta & Oranges
    (64, '#FF4500', 'Red Orange', 0, 64),
    (65, '#FF5722', 'Deep Orange', 0, 65),
    (66, '#F4511E', 'Flame', 0, 66),
    (67, '#E64A19', 'Rust Orange', 0, 67),
    (68, '#D84315', 'Burnt Terracotta', 0, 68),
    (69, '#BF360C', 'Dark Auburn', 0, 69),
    (70, '#FF6F00', 'Tangerine', 0, 70),
    (71, '#FF8F00', 'Dark Amber', 0, 71),
    (72, '#FFA000', 'Marigold', 0, 72),
    (73, '#FFB300', 'Goldenrod', 0, 73),
    (74, '#FFA726', 'Pastel Orange', 0, 74),
    (75, '#FF9800', 'Cadmium Orange', 0, 75),
    (76, '#FB8C00', 'Tiger Orange', 0, 76),
    (77, '#F57C00', 'Dark Coral', 0, 77),
    (78, '#E65100', 'Dark Bronze', 0, 78),
    (79, '#FFE0B2', 'Cream Orange', 0, 79),

    -- 80..95: Yellows, Golds & Khakis
    (80, '#FFF8E1', 'Soft Butter', 0, 80),
    (81, '#FFF9C4', 'Pale Lemon', 0, 81),
    (82, '#FFF59D', 'Banana Yellow', 0, 82),
    (83, '#FFEE58', 'Bright Canary', 0, 83),
    (84, '#FFEB3B', 'Cyber Yellow', 0, 84),
    (85, '#FDD835', 'Dandelion', 0, 85),
    (86, '#FBC02D', 'Mustard', 0, 86),
    (87, '#F9A825', 'Deep Gold', 0, 87),
    (88, '#F57F17', 'Dark Ochre', 0, 88),
    (89, '#C0CA33', 'Citron', 0, 89),
    (90, '#D4E157', 'Bright Olive', 0, 90),
    (91, '#CDDC39', 'Chartreuse', 0, 91),
    (92, '#DCE775', 'Light Lime', 0, 92),
    (93, '#E6EE9C', 'Pale Celadon', 0, 93),
    (94, '#F0F4C3', 'Lemon Chiffon', 0, 94),
    (95, '#BDB76B', 'Dark Khaki', 0, 95),

    -- 96..111: Spring, Lime & Vibrant Greens
    (96, '#76FF03', 'Electric Lime', 0, 96),
    (97, '#64DD17', 'Neon Green', 0, 97),
    (98, '#00E676', 'Spring Green', 0, 98),
    (99, '#69F0AE', 'Aquamarine Light', 0, 99),
    (100, '#B9F6CA', 'Pale Mint', 0, 100),
    (101, '#AEEA00', 'Acid Green', 0, 101),
    (102, '#8BC34A', 'Leaf Green', 0, 102),
    (103, '#7CB342', 'Apple Green', 0, 103),
    (104, '#689F38', 'Kelly Green', 0, 104),
    (105, '#558B2F', 'Moss Green', 0, 105),
    (106, '#33691E', 'Deep Avocado', 0, 106),
    (107, '#4CAF50', 'Emerald Mid', 0, 107),
    (108, '#43A047', 'Clover', 0, 108),
    (109, '#388E3C', 'Grass Green', 0, 109),
    (110, '#2E7D32', 'Spruce Green', 0, 110),
    (111, '#1B5E20', 'Dark Pine', 0, 111),

    -- 112..127: Deep, Sage, Sea & Forest Greens
    (112, '#004D40', 'Deep Sea Pine', 0, 112),
    (113, '#00695C', 'Dark Teal Green', 0, 113),
    (114, '#00796B', 'Amazon Green', 0, 114),
    (115, '#00897B', 'Pine Teal', 0, 115),
    (116, '#009688', 'Jade Green', 0, 116),
    (117, '#26A69A', 'Mountain Lake', 0, 117),
    (118, '#4DB6AC', 'Soft Teal', 0, 118),
    (119, '#80CBC4', 'Pale Sage', 0, 119),
    (120, '#B2DFDB', 'Ice Teal', 0, 120),
    (121, '#E0F2F1', 'Frost Green', 0, 121),
    (122, '#2E4A3E', 'Evergreen', 0, 122),
    (123, '#1F382B', 'Deep Jungle', 0, 123),
    (124, '#3B5323', 'Army Green', 0, 124),
    (125, '#4A5D23', 'Camo Green', 0, 125),
    (126, '#556B2F', 'Dark Olive Green', 0, 126),
    (127, '#8FBC8F', 'Dark Sea Green', 0, 127),

    -- 128..143: Cyans, Aquas & Ice Blues
    (128, '#00B4D8', 'Pacific Blue', 0, 128),
    (129, '#0096C7', 'Cerulean', 0, 129),
    (130, '#0077B6', 'Deep Cyan', 0, 130),
    (131, '#023E8A', 'Adriatic Blue', 0, 131),
    (132, '#03045E', 'Midnight Abyss', 0, 132),
    (133, '#90E0EF', 'Sky Tint', 0, 133),
    (134, '#ADE8F4', 'Glacial Blue', 0, 134),
    (135, '#CAF0F8', 'Ice Blue', 0, 135),
    (136, '#00E5FF', 'Cyan Neon', 0, 136),
    (137, '#18FFFF', 'Electric Aqua', 0, 137),
    (138, '#84FFFF', 'Light Laser Cyan', 0, 138),
    (139, '#E0F7FA', 'Polar Mist', 0, 139),
    (140, '#26C6DA', 'Robin Egg', 0, 140),
    (141, '#00ACC1', 'Wave Blue', 0, 141),
    (142, '#0097A7', 'Nordic Blue', 0, 142),
    (143, '#00838F', 'Marine Blue', 0, 143),

    -- 144..159: Sky, Cobalt & Royal Blues
    (144, '#006064', 'Deep Arctic', 0, 144),
    (145, '#29B6F6', 'Horizon Blue', 0, 145),
    (146, '#03A9F4', 'Summer Sky', 0, 146),
    (147, '#039BE5', 'Vivid Azure', 0, 147),
    (148, '#0288D1', 'Deep Azure', 0, 148),
    (149, '#0277BD', 'Baltic Blue', 0, 149),
    (150, '#01579B', 'Navy Blue', 0, 150),
    (151, '#81D4FA', 'Baby Blue', 0, 151),
    (152, '#B3E5FC', 'Powder Blue', 0, 152),
    (153, '#E1F5FE', 'Pale Sky', 0, 153),
    (154, '#4FC3F7', 'Cornflower Tint', 0, 154),
    (155, '#448AFF', 'Electric Cobalt', 0, 155),
    (156, '#2979FF', 'Brilliant Blue', 0, 156),
    (157, '#2962FF', 'Pure Blue', 0, 157),
    (158, '#1565C0', 'Sapphire', 0, 158),
    (159, '#0D47A1', 'Midnight Navy', 0, 159),

    -- 160..175: Indigo, Deep Blues & Midnight Violets
    (160, '#1A237E', 'Abyss Indigo', 0, 160),
    (161, '#283593', 'Dark Indigo', 0, 161),
    (162, '#303F9F', 'Steel Indigo', 0, 162),
    (163, '#3949AB', 'Royal Indigo', 0, 163),
    (164, '#3F51B5', 'Slate Indigo', 0, 164),
    (165, '#5C6BC0', 'Periwinkle Blue', 0, 165),
    (166, '#7986CB', 'Iris Blue', 0, 166),
    (167, '#9FA8DA', 'Pale Periwinkle', 0, 167),
    (168, '#C5CAE9', 'Lavender Mist', 0, 168),
    (169, '#E8EAF6', 'Ghost Lavender', 0, 169),
    (170, '#536DFE', 'Neon Indigo', 0, 170),
    (171, '#3D5AFE', 'Electric Periwinkle', 0, 171),
    (172, '#1A1C40', 'Space Cadet', 0, 172),
    (173, '#141829', 'Dark Nebula', 0, 173),
    (174, '#241734', 'Dark Void', 0, 174),
    (175, '#311432', 'Blackcurrant', 0, 175),

    -- 176..191: Purples, Violets & Amethysts
    (176, '#4A148C', 'Deep Violet', 0, 176),
    (177, '#6A1B9A', 'Imperial Purple', 0, 177),
    (178, '#7B1FA2', 'Majestic Purple', 0, 178),
    (179, '#8E24AA', 'Orchid Violet', 0, 179),
    (180, '#9C27B0', 'Vivid Purple', 0, 180),
    (181, '#AB47BC', 'Lilac', 0, 181),
    (182, '#BA68C8', 'Bright Lavender', 0, 182),
    (183, '#CE93D8', 'Pastel Violet', 0, 183),
    (184, '#E1BEE7', 'Soft Lavender', 0, 184),
    (185, '#F3E5F5', 'Whisper Purple', 0, 185),
    (186, '#AA00FF', 'Neon Violet', 0, 186),
    (187, '#D500F9', 'Neon Magenta', 0, 187),
    (188, '#E040FB', 'Bright Fuchsia', 0, 188),
    (189, '#EA80FC', 'Bubble Lavender', 0, 189),
    (190, '#581845', 'Dark Boysenberry', 0, 191),
    (191, '#900C3F', 'Mulberry Wine', 0, 191),

    -- 192..207: Pinks, Magentas & Roses
    (192, '#880E4F', 'Burgundy Plum', 0, 192),
    (193, '#AD1457', 'Dark Raspberry', 0, 193),
    (194, '#C2185B', 'Raspberry', 0, 194),
    (195, '#D81B60', 'Rose Red', 0, 195),
    (196, '#E91E63', 'Crimson Pink', 0, 196),
    (197, '#EC407A', 'Watermelon', 0, 197),
    (198, '#F06292', 'Barbie Pink', 0, 198),
    (199, '#F48FB1', 'Baby Pink', 0, 199),
    (200, '#F8BBD0', 'Soft Pink', 0, 200),
    (201, '#FCE4EC', 'Cotton Candy', 0, 201),
    (202, '#FF1493', 'Deep Hot Pink', 0, 202),
    (203, '#FF69B4', 'Hot Pink', 0, 203),
    (204, '#C71585', 'Medium Violet Red', 0, 204),
    (205, '#DB7093', 'Pale Violet Red', 0, 205),
    (206, '#FFB6C1', 'Light Pink', 0, 206),
    (207, '#FFC0CB', 'Classic Pink', 0, 207),

    -- 208..223: Earth, Browns & Wood Tones
    (208, '#3E2723', 'Dark Espresso', 0, 208),
    (209, '#4E342E', 'Chocolate', 0, 209),
    (210, '#5D4037', 'Roasted Walnut', 0, 210),
    (211, '#6D4C41', 'Dark Chestnut', 0, 211),
    (212, '#795548', 'Brown Leather', 0, 212),
    (213, '#8D6E63', 'Cocoa', 0, 213),
    (214, '#A1887F', 'Milk Chocolate', 0, 214),
    (215, '#BCAAA4', 'Taupe', 0, 215),
    (216, '#D7CCC8', 'Almond Cream', 0, 216),
    (217, '#EFEBE9', 'Pale Taupe', 0, 217),
    (218, '#4A2C11', 'Dark Hickory', 0, 218),
    (219, '#653818', 'Cedar', 0, 219),
    (220, '#824A1E', 'Mahogany', 0, 220),
    (221, '#9E5D28', 'Russet', 0, 221),
    (222, '#B77032', 'Copper Brown', 0, 222),
    (223, '#CF843E', 'Caramel', 0, 223),

    -- 224..239: Skin Tones, Sands, Warm Beiges & Tans
    (224, '#E49A4C', 'Sandalwood', 0, 224),
    (225, '#F0B266', 'Honey', 0, 225),
    (226, '#F9CA85', 'Golden Sand', 0, 226),
    (227, '#FFE2A8', 'Warm Sand', 0, 227),
    (228, '#FFF0CD', 'Champagne', 0, 228),
    (229, '#3B2219', 'Deep Ebony Skin', 0, 229),
    (230, '#543224', 'Dark Mocha Skin', 0, 230),
    (231, '#704432', 'Rich Brown Skin', 0, 231),
    (232, '#8C5741', 'Warm Bronze Skin', 0, 232),
    (233, '#A96D52', 'Chestnut Skin', 0, 233),
    (234, '#C38466', 'Golden Tan Skin', 0, 234),
    (235, '#D89B7D', 'Medium Tan Skin', 0, 235),
    (236, '#E8B498', 'Peach Ivory Skin', 0, 236),
    (237, '#F3CEB6', 'Light Peach Skin', 0, 237),
    (238, '#FAE3D4', 'Fair Porcelain Skin', 0, 238),
    (239, '#FFF5EE', 'Seashell', 0, 239),

    -- 240..255: Cyberpunk, High-Vis & Specialty Accents
    (240, '#39FF14', 'Cyber Neon Green', 0, 240),
    (241, '#00FFCC', 'Cyberpunk Mint', 0, 241),
    (242, '#00FFFF', 'Laser Aqua', 0, 242),
    (243, '#0080FF', 'Electric Sky', 0, 243),
    (244, '#7B00FF', 'Ultraviolet', 0, 244),
    (245, '#FF00FF', 'Acid Magenta', 0, 245),
    (246, '#FF007F', 'Fluorescent Rose', 0, 246),
    (247, '#FF3300', 'Blaze Orange', 0, 247),
    (248, '#FFFF00', 'Pure Yellow', 0, 248),
    (249, '#CCFF00', 'High-Vis Lime', 0, 249),
    (250, '#FF0055', 'Laser Red', 0, 250),
    (251, '#7600EC', 'Neon Purple', 0, 251),
    (252, '#00FF88', 'Toxic Spring', 0, 252),
    (253, '#FFE600', 'Cyberpunk Yellow', 0, 253),
    (254, '#05D9E8', 'Neon Cyan', 0, 254),
    (255, '#FF2A6D', 'Synthwave Pink', 0, 255)
) AS src (id, hex_code, name, is_active, sort_order)
ON target.id = src.id
WHEN NOT MATCHED THEN
    INSERT (id, hex_code, name, is_active, sort_order)
    VALUES (src.id, src.hex_code, src.name, src.is_active, src.sort_order);
GO

-- Migrate foreign key constraints on pixel_placements to reference canvas_palette(id)
DECLARE @fkName NVARCHAR(200);
SELECT @fkName = name 
FROM sys.foreign_keys 
WHERE parent_object_id = OBJECT_ID('pixel_placements') 
  AND referenced_object_id = OBJECT_ID('color_palette');

IF @fkName IS NOT NULL
BEGIN
    EXEC('ALTER TABLE pixel_placements DROP CONSTRAINT [' + @fkName + ']');
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys 
    WHERE parent_object_id = OBJECT_ID('pixel_placements') 
      AND referenced_object_id = OBJECT_ID('canvas_palette')
)
BEGIN
    ALTER TABLE pixel_placements 
    ADD CONSTRAINT FK_pixel_placements_canvas_palette 
    FOREIGN KEY (color_id) REFERENCES canvas_palette(id);
END
GO

-- Sync legacy color_palette table with all 256 colors for complete backward compatibility
IF EXISTS (SELECT * FROM sys.tables WHERE name = 'color_palette')
BEGIN
    INSERT INTO color_palette (color_id, hex_code, name)
    SELECT id, hex_code, name 
    FROM canvas_palette
    WHERE id NOT IN (SELECT color_id FROM color_palette);
END
GO

