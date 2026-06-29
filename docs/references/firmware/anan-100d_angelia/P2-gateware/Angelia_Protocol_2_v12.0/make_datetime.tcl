# Make datetime.vhd file from Tcl script

# Current date, time, and seconds since epoch
# Array index                                            0  1  2  3  4  5  6
set datetime_arr [clock format [clock seconds] -format {%Y %m %d %H %M %S %s}]

set filename datetime.vhd
set file [open $filename w]
puts $file "library ieee;"
puts $file "use ieee.std_logic_1164.all;"
puts $file "use ieee.std_logic_arith.all;"
puts $file ""
puts $file "entity datetime is"
puts $file "  port("
puts $file "  --  Day   : out std_logic_vector(4 downto 0);"
puts $file "  --  Month : out std_logic_vector(3 downto 0);"
puts $file "  --  Year  : out std_logic_vector(4 downto 0));"
puts $file "  Epoch_min  : out std_logic_vector(25 downto 0));"
puts $file "  -- Date information"
puts $file "  constant YEAR_INT  : integer                       := [lindex $datetime_arr 0];"
puts $file "  constant YEAR_HEX  : std_logic_vector(15 downto 0) := X\"[lindex $datetime_arr 0]\";"
puts $file "  constant MONTH_INT : integer                       := [lindex $datetime_arr 1];"
puts $file "  constant MONTH_HEX : std_logic_vector(7 downto 0)  := X\"[lindex $datetime_arr 1]\";"
puts $file "  constant DAY_INT   : integer                       := [lindex $datetime_arr 2];"
puts $file "  constant DAY_HEX   : std_logic_vector(7 downto 0)  := X\"[lindex $datetime_arr 2]\";"
puts $file "  constant DATE_HEX  : std_logic_vector(31 downto 0) := YEAR_HEX & MONTH_HEX & DAY_HEX;"
puts $file "  -- Time information"
puts $file "  constant HOUR_INT   : integer                       := [lindex $datetime_arr 3];"
puts $file "  constant HOUR_HEX   : std_logic_vector(7 downto 0)  := X\"[lindex $datetime_arr 3]\";"
puts $file "  constant MINUTE_INT : integer                       := [lindex $datetime_arr 4];"
puts $file "  constant MINUTE_HEX : std_logic_vector(7 downto 0)  := X\"[lindex $datetime_arr 4]\";"
puts $file "  constant SECOND_INT : integer                       := [lindex $datetime_arr 5];"
puts $file "  constant SECOND_HEX : std_logic_vector(7 downto 0)  := X\"[lindex $datetime_arr 5]\";"
puts $file "  constant TIME_HEX   : std_logic_vector(31 downto 0) := X\"00\" & HOUR_HEX & MINUTE_HEX & SECOND_HEX;"
puts $file "  -- Miscellaneous information"
puts $file "  constant EPOCH_INT  : integer := [lindex $datetime_arr 6];  -- Seconds since 1970-01-01_00:00:00"
puts $file ""
puts $file ""
puts $file "end datetime;"
puts $file ""
puts $file "architecture rtl of datetime is"
puts $file "begin"
puts $file "--  Day   <= conv_std_logic_vector(datetime.day_int, 5);"
puts $file "--  Month <= conv_std_logic_vector(datetime.month_int, 4);"
puts $file "--  Year  <= conv_std_logic_vector(datetime.year_int mod 100, 5);"
puts $file "  Epoch_min  <= conv_std_logic_vector(datetime.epoch_int / 60, 26);"
puts $file "end architecture rtl;"
close $file

