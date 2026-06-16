	.zeropage	__rc0
	.zeropage	__rc1
	.zeropage	__rc2
	.zeropage	__rc3
	.zeropage	__rc4
	.zeropage	__rc5
	.zeropage	__rc6
	.zeropage	__rc7
	.zeropage	__rc8
	.zeropage	__rc9
	.zeropage	__rc10
	.zeropage	__rc11
	.zeropage	__rc12
	.zeropage	__rc13
	.zeropage	__rc14
	.zeropage	__rc15
	.zeropage	__rc16
	.zeropage	__rc17
	.zeropage	__rc18
	.zeropage	__rc19
	.zeropage	__rc20
	.zeropage	__rc21
	.zeropage	__rc22
	.zeropage	__rc23
	.zeropage	__rc24
	.zeropage	__rc25
	.zeropage	__rc26
	.zeropage	__rc27
	.zeropage	__rc28
	.zeropage	__rc29
	.zeropage	__rc30
	.zeropage	__rc31
	.file	"gcd.c"
	.section	.text.gcd,"ax",@progbits
	.globl	gcd                             ; -- Begin function gcd
	.type	gcd,@function
gcd:                                    ; @gcd
; %bb.0:
	sta	__rc16
	lda	__rc20
	pha
	lda	__rc21
	pha
	lda	__rc16
	ldy	__rc2
	inc	__rc3
	dec	__rc3
	bne	.LBB0_2
; %bb.1:
	cpy	#0
	beq	.LBB0_5
.LBB0_2:                                ; =>This Inner Loop Header: Depth=1
	sty	__rc17
	ldy	__rc3
	sty	__rc21
	ldy	__rc17
	sty	__rc20
	sty	__rc2
	jsr	__modhi3
	tay
	stx	__rc3
	lda	__rc20
	ldx	__rc21
	inc	__rc3
	dec	__rc3
	bne	.LBB0_2
; %bb.3:                                ;   in Loop: Header=BB0_2 Depth=1
	cpy	#0
	bne	.LBB0_2
.LBB0_4:
	ldx	__rc21
	lda	__rc20
	sta	__rc16
	pla
	sta	__rc21
	pla
	sta	__rc20
	lda	__rc16
	rts
.LBB0_5:
	sta	__rc20
	stx	__rc21
	jmp	.LBB0_4
.Lfunc_end0:
	.size	gcd, .Lfunc_end0-gcd
                                        ; -- End function
	.ident	"clang version 23.0.0git (https://github.com/llvm-mos/llvm-mos.git c798c31416f72b395c658b5502d281a162387ab1)"
	.section	".note.GNU-stack","",@progbits
	.addrsig
	;Declaring this symbol tells the CRT that the stack pointer needs to be initialized.
	.globl	__do_init_stack
