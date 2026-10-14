# cs-pw-helper: build system
#
# Builds the Wayland screen capture helper that talks
# to xdg-desktop-portal + PipeWire and writes raw frames
# to a Unix socket for ffmpeg to consume.
#
# System packages (Ubuntu 24.04 / Debian 13):
#   sudo apt install build-essential libpipewire-0.3-dev \
#                        libportal-dev libportal-gtk3-dev \
#                        libgtk-3-dev pkg-config
#
# Run `make` to build. Run `make install` to copy the
# binary to tools/linux-x64/ (which is what the .deb
# and AppImage bundle).

CC      ?= cc
CFLAGS  ?= -O2 -Wall -Wextra -Wno-unused-parameter \
           -Wno-unused-function -Wno-unused-variable \
           -std=c11 -D_GNU_SOURCE
LDFLAGS ?=

PKG_CONFIG ?= pkg-config
PKGS        = libportal-gtk3 libportal libpipewire-0.3 gtk+-3.0

PREFIX     ?= /usr/local
BINDIR     ?= $(PREFIX)/bin

SRC  = cs-pw-helper.c
OBJ  = $(SRC:.c=.o)
BIN  = cs-pw-helper

# Pull cflags and libs from pkg-config.
PKG_CFLAGS  := $(shell $(PKG_CONFIG) --cflags $(PKGS) 2>/dev/null)
PKG_LIBS    := $(shell $(PKG_CONFIG) --libs   $(PKGS) 2>/dev/null)

# Fallback if pkg-config can't find a package (the
# build will fail with a clear error rather than
# silently).
ifeq ($(PKG_CFLAGS),)
  $(warning pkg-config could not find one of: $(PKGS))
  $(warning Install: libpipewire-0.3-dev libportal-dev libportal-gtk3-dev)
endif

CFLAGS  += $(PKG_CFLAGS)
LIBS    := $(PKG_LIBS) -lpthread

.PHONY: all clean install

all: $(BIN)

$(BIN): $(OBJ)
	$(CC) $(CFLAGS) -o $@ $< $(LDFLAGS) $(LIBS)

%.o: %.c
	$(CC) $(CFLAGS) -c -o $@ $<

install: $(BIN)
	install -d $(DESTDIR)$(BINDIR)
	install -m 0755 $(BIN) $(DESTDIR)$(BINDIR)/$(BIN)

clean:
	rm -f $(OBJ) $(BIN)
